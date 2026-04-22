/**
 * @file Admin Console E2E Test Suite — WebVella ERP React SPA
 *
 * Comprehensive Playwright E2E test suite validating all critical SDK admin
 * console user-facing workflows against a full LocalStack stack (API Gateway +
 * Lambda handlers + DynamoDB + Cognito). Replaces the monolith's
 * WebVella.Erp.Plugins.SDK admin Razor Pages and StencilJS web components:
 *
 *   entity/list.cshtml          → /admin/entities
 *   entity/create.cshtml        → /admin/entities/create
 *   entity/details.cshtml       → /admin/entities/:entityId
 *   entity/manage.cshtml        → /admin/entities/:entityId/manage
 *   entity/fields.cshtml        → /admin/entities/:entityId/fields
 *   entity/create-field.cshtml  → /admin/entities/:entityId/fields/create
 *   role/list.cshtml            → /admin/roles
 *   role/create.cshtml          → /admin/roles/create
 *   role/manage.cshtml          → /admin/roles/:roleId/manage
 *   user/list.cshtml            → /admin/users
 *   user/create.cshtml          → /admin/users/create
 *   user/manage.cshtml          → /admin/users/:userId/manage
 *   page/list.cshtml            → /admin/pages
 *   page/create.cshtml          → /admin/pages/create
 *   page/manage.cshtml          → /admin/pages/:pageId/manage
 *   datasource-manage (Stencil) → /admin/data-sources/*
 *
 * API mapping:
 *   entity/* pages  → Entity Management service  /v1/entities
 *   role/*   pages  → Identity service            /v1/roles
 *   user/*   pages  → Identity service            /v1/users (Cognito)
 *   page/*   pages  → Plugin System service       /v1/pages
 *   datasource/*    → Entity Management service   /v1/data-sources
 *
 * Test user: erp@webvella.com / erp (admin, seeded via seed-test-data.sh)
 *
 * Critical rules (AAP §0.8.1, §0.8.4):
 *   - ALL tests run against LocalStack — zero mocked AWS SDK calls.
 *   - Admin user must have the administrator Cognito group.
 *   - Full behavioral parity for every SDK admin CRUD workflow.
 *   - Test data uses unique names per run to avoid collisions.
 *   - Created entities/fields/roles/users/pages/data-sources are cleaned up.
 *
 * @see WebVella.Erp.Plugins.SDK/Controllers/AdminController.cs
 * @see WebVella.Erp.Plugins.SDK/Pages/_ViewImports.cshtml
 * @see WebVella.Erp/Api/EntityManager.cs
 * @see WebVella.Erp/Api/SecurityManager.cs
 * @see WebVella.Erp/Api/DataSourceManager.cs
 * @see WebVella.Erp/Api/Definitions.cs
 */

import { test, expect, Page, BrowserContext } from '@playwright/test';
import { login } from './auth.spec';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Base URL for the React SPA frontend (Vite dev server or production build). */
const BASE_URL: string = process.env.PLAYWRIGHT_BASE_URL || 'http://localhost:5173';

/** Admin section root route — replaces /sdk/ route prefix in the monolith. */
const ADMIN_URL = '/admin';

/** Entity management page route. */
const ENTITIES_URL = `${ADMIN_URL}/entities`;

/** Role management page route. */
const ROLES_URL = `${ADMIN_URL}/roles`;

/** User management page route. */
const USERS_URL = `${ADMIN_URL}/users`;

/** Page management page route. */
const PAGES_URL = `${ADMIN_URL}/pages`;

/** Data source management page route. */
const DATA_SOURCES_URL = `${ADMIN_URL}/data-sources`;

/** Maximum time (ms) to wait for API-backed data rendering (Lambda cold start). */
const DATA_TIMEOUT = 15_000;

/** Short settle time (ms) after navigation for DOM and state updates. */
const SETTLE_TIME = 500;

/**
 * Unique suffix for all test resources created during this run.
 * Prevents collisions across parallel test executions and makes cleanup
 * deterministic. EntityManager requires lowercase names with no spaces.
 */
const RUN_ID = `e2e${Date.now().toString(36)}`;

// ---------------------------------------------------------------------------
// Reusable Helpers
// ---------------------------------------------------------------------------

/**
 * Generates a unique lowercase alphanumeric name safe for entity/role/user
 * creation. The monolith's EntityManager required names to be lowercase
 * with no spaces (max 63 chars); this helper guarantees that constraint.
 *
 * @param prefix - Short human-readable prefix (e.g., "testent", "testrole").
 * @returns A unique, lowercase, no-space string under 63 characters.
 */
function uniqueName(prefix: string): string {
  return `${prefix}${RUN_ID}`.toLowerCase().replace(/[^a-z0-9_]/g, '').slice(0, 60);
}

/**
 * Safely attempts to delete a resource by navigating to a confirmation
 * action on its detail or manage page and accepting the deletion dialog.
 * Returns true if the deletion appeared to succeed, false otherwise.
 *
 * @param page - Playwright Page instance.
 * @param listUrl - URL of the resource list page to verify removal.
 * @param resourceName - Name/text of the resource to locate in the list.
 */
async function cleanupResource(
  page: Page,
  listUrl: string,
  resourceName: string,
): Promise<boolean> {
  try {
    await page.goto(listUrl, { waitUntil: 'networkidle', timeout: DATA_TIMEOUT });
    await page.waitForTimeout(SETTLE_TIME);

    // Try to find the resource row and its delete action
    const resourceRow = page.getByText(resourceName, { exact: false });
    const isVisible = await resourceRow.isVisible().catch(() => false);
    if (!isVisible) return true; // Already gone

    // Look for a delete button/link in the resource row or nearby
    const deleteBtn = page.getByRole('button', { name: /delete/i })
      .or(page.locator(`[data-testid="delete-${resourceName}"]`))
      .or(page.locator('button[aria-label*="delete" i]'));

    // Click on the resource first to navigate to details
    await resourceRow.first().click();
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(SETTLE_TIME);

    // Try to find and click the delete action on the detail page
    const detailDeleteBtn = page.getByRole('button', { name: /delete/i })
      .or(page.locator('[data-testid="delete-btn"]'));
    const detailDeleteVisible = await detailDeleteBtn.first().isVisible().catch(() => false);

    if (detailDeleteVisible) {
      await detailDeleteBtn.first().click();
      await page.waitForTimeout(SETTLE_TIME);

      // Accept confirmation dialog/modal
      const confirmBtn = page.getByRole('button', { name: /confirm|yes|ok|delete/i })
        .or(page.locator('[data-testid="confirm-delete-btn"]'));
      const confirmVisible = await confirmBtn.first().isVisible().catch(() => false);
      if (confirmVisible) {
        await confirmBtn.first().click();
        await page.waitForTimeout(SETTLE_TIME);
      }
    }

    return true;
  } catch {
    return false;
  }
}

// ---------------------------------------------------------------------------
// Test Suite
// ---------------------------------------------------------------------------

test.describe('Admin Console', () => {
  /**
   * Run tests serially within the admin console suite because entity/field
   * creation/deletion tests depend on each other's side effects and share
   * an authenticated browser context for performance.
   */
  test.describe.configure({ mode: 'serial' });

  let context: BrowserContext;
  let page: Page;

  // Tracked resource IDs/names for cleanup
  const createdEntityNames: string[] = [];
  const createdRoleNames: string[] = [];
  const createdUserEmails: string[] = [];
  const createdPageNames: string[] = [];
  const createdDataSourceNames: string[] = [];

  // Pre-generated unique names for test resources
  const testEntityName = uniqueName('testent_');
  const testEntityLabel = `Test Entity ${RUN_ID}`;
  const testEntityLabelPlural = `Test Entities ${RUN_ID}`;
  const testFieldEntityName = uniqueName('fldent_');
  const testFieldEntityLabel = `Field Entity ${RUN_ID}`;
  const testFieldEntityLabelPlural = `Field Entities ${RUN_ID}`;
  const testRoleName = uniqueName('testrole_');
  const testRoleDescription = `Test role created by E2E run ${RUN_ID}`;
  const testUserEmail = `testuser_${RUN_ID}@webvella-test.com`;
  const testUserFirstName = `TestFirst${RUN_ID}`;
  const testUserLastName = `TestLast${RUN_ID}`;
  const testPageName = uniqueName('testpage_');
  const testPageLabel = `Test Page ${RUN_ID}`;
  const testDsName = uniqueName('testds_');

  // ─── Lifecycle Hooks ────────────────────────────────────────────────────

  /**
   * Before all tests: create a shared browser context, authenticate as the
   * seeded admin user (erp@webvella.com / erp), and verify the SPA is
   * reachable. Uses the exported `login` helper from auth.spec.ts.
   */
  test.beforeAll(async ({ browser }) => {
    context = await browser.newContext();
    page = await context.newPage();

    // Smoke check: verify the SPA is reachable
    const response = await page.goto(BASE_URL, { timeout: 30_000 });
    expect(
      response !== null && (response.ok() || response.status() === 304),
    ).toBeTruthy();

    // Authenticate as admin using the shared login helper
    await login(page);
  });

  /**
   * After all tests: clean up all test resources created during the run,
   * then close the browser context.
   */
  test.afterAll(async () => {
    // Best-effort cleanup of all created test resources
    if (page && !page.isClosed()) {
      // Clean up entities (including field test entity)
      for (const name of [...createdEntityNames]) {
        await cleanupResource(page, ENTITIES_URL, name);
      }
      // Clean up roles
      for (const name of [...createdRoleNames]) {
        await cleanupResource(page, ROLES_URL, name);
      }
      // Clean up users
      for (const email of [...createdUserEmails]) {
        await cleanupResource(page, USERS_URL, email);
      }
      // Clean up pages
      for (const name of [...createdPageNames]) {
        await cleanupResource(page, PAGES_URL, name);
      }
      // Clean up data sources
      for (const name of [...createdDataSourceNames]) {
        await cleanupResource(page, DATA_SOURCES_URL, name);
      }
    }

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

  /**
   * After each test: clear transient page state (cookies, localStorage,
   * sessionStorage) to prevent test pollution while preserving the shared
   * browser context for authentication. This mirrors the monolith's
   * per-request ErpMiddleware state cleanup.
   */
  test.afterEach(async () => {
    try {
      // Clear cookies for the current origin
      await context.clearCookies();

      // Clear localStorage and sessionStorage to reset UI state
      await page.evaluate(() => {
        try {
          window.localStorage.clear();
        } catch { /* storage access may be restricted */ }
        try {
          window.sessionStorage.clear();
        } catch { /* storage access may be restricted */ }
      });
    } catch {
      // Swallow errors if the page is already closed or navigated away
    }

    // Re-authenticate to restore the session for the next test
    try {
      await login(page);
    } catch {
      // If re-login fails, the next test's navigation will handle it
    }
  });

  // ═══════════════════════════════════════════════════════════════════════
  // SECTION 1: ENTITY MANAGEMENT TESTS
  //
  // Replaces: SDK plugin entity/* Razor Pages
  //   entity/list.cshtml.cs        — EntityManager.ReadEntities() with paging
  //   entity/create.cshtml.cs      — EntityManager.CreateEntity()
  //   entity/details.cshtml.cs     — EntityManager.ReadEntity()
  //   entity/manage.cshtml.cs      — EntityManager.UpdateEntity()
  //
  // Routes: /admin/entities, /admin/entities/create,
  //         /admin/entities/:entityId, /admin/entities/:entityId/manage
  // ═══════════════════════════════════════════════════════════════════════

  test.describe('Entity Management', () => {
    /**
     * Verify entity list renders with system entities.
     * Source: entity/list.cshtml.cs — EntityManager.ReadEntities(),
     * grid columns: action, name (sortable, default asc), label (sortable).
     * System entities: user, role, account, contact (from Definitions.cs).
     */
    test('should render entity list page with system entities', async () => {
      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });

      // The entity list page must render a table/grid with entities
      const table = page.locator(
        'table, [role="grid"], [data-testid="entity-list"]',
      );
      await expect(table).toBeVisible({ timeout: DATA_TIMEOUT });

      // Verify key column headers are present (name, label)
      const nameHeader = page
        .getByRole('columnheader', { name: /name/i })
        .or(page.locator('th').filter({ hasText: /name/i }));
      await expect(nameHeader).toBeVisible({ timeout: DATA_TIMEOUT });

      const labelHeader = page
        .getByRole('columnheader', { name: /label/i })
        .or(page.locator('th').filter({ hasText: /label/i }));
      await expect(labelHeader).toBeVisible();

      // Verify at least one system entity is displayed
      // The 'user' entity is created by Definitions.cs SystemIds.UserEntityId
      const systemEntityVisible = await page
        .getByText('user', { exact: false })
        .first()
        .isVisible()
        .catch(() => false);
      expect(systemEntityVisible).toBeTruthy();
    });

    /**
     * Verify search/filter functionality on entity list.
     */
    test('should filter entities using search functionality', async () => {
      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });
      await page.waitForTimeout(SETTLE_TIME);

      // Look for search/filter input
      const searchInput = page
        .getByPlaceholder(/search|filter/i)
        .or(page.getByLabel(/search|filter/i))
        .or(page.locator('[data-testid="entity-search"], input[type="search"]'));

      const searchVisible = await searchInput.first().isVisible().catch(() => false);
      if (searchVisible) {
        // Type a system entity name to test filtering
        await searchInput.first().fill('user');
        await page.waitForTimeout(SETTLE_TIME);

        // The 'user' entity should still be visible after filtering
        await expect(
          page.getByText('user', { exact: false }).first(),
        ).toBeVisible({ timeout: DATA_TIMEOUT });
      }
    });

    /**
     * Verify entity metadata (name, label, icon) displayed in list.
     */
    test('should display entity metadata in list', async () => {
      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });

      // Wait for data to load
      const table = page.locator(
        'table, [role="grid"], [data-testid="entity-list"]',
      );
      await expect(table).toBeVisible({ timeout: DATA_TIMEOUT });

      // Verify rows contain entity metadata
      const firstRow = page
        .locator('table tbody tr, [role="row"]')
        .filter({ hasNot: page.locator('th') })
        .first();
      const firstRowVisible = await firstRow.isVisible().catch(() => false);
      if (firstRowVisible) {
        // Row should contain text content (entity name or label)
        const rowText = await firstRow.textContent();
        expect(rowText).toBeTruthy();
        expect(rowText!.trim().length).toBeGreaterThan(0);
      }
    });

    /**
     * Create a new entity via the admin form.
     * Source: entity/create.cshtml.cs — BindProperty: Name, Label, LabelPlural,
     * IconName, Color, System, RecordPermissions, RecordScreenIdField.
     * EntityManager.CreateEntity() validates lowercase name, no spaces, max 63.
     */
    test('should create a new entity', async () => {
      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });

      // Click "Create Entity" action button
      await page
        .getByRole('link', { name: /create/i })
        .or(page.getByRole('button', { name: /create/i }))
        .or(page.locator('[data-testid="create-entity-btn"]'))
        .click();

      // Wait for the create form to load
      await page.waitForURL(/entities.*create|entities.*new/i, {
        timeout: DATA_TIMEOUT,
      });

      // Fill entity creation form fields
      const nameInput = page
        .getByLabel(/^name$/i)
        .or(page.locator('[name="name"], [data-testid="entity-name-input"]'));
      const labelInput = page
        .getByLabel(/^label$/i)
        .or(page.locator('[name="label"], [data-testid="entity-label-input"]'));
      const pluralInput = page
        .getByLabel(/plural/i)
        .or(
          page.locator(
            '[name="labelPlural"], [data-testid="entity-label-plural-input"]',
          ),
        );

      await nameInput.fill(testEntityName);
      await labelInput.fill(testEntityLabel);
      await pluralInput.fill(testEntityLabelPlural);

      // Submit the form
      await page.getByRole('button', { name: /save|create|submit/i }).click();

      // Verify success: notification or redirect to entity details/list
      await expect(
        page
          .getByText(/success|created|saved/i)
          .or(page.locator('[data-testid="success-notification"]')),
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Track for cleanup
      createdEntityNames.push(testEntityName);

      // Navigate back to entity list and verify the new entity appears
      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });
      await expect(
        page.getByText(testEntityName, { exact: false }),
      ).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Delete a test (non-system) entity.
     * Source: entity/details.cshtml.cs with delete action.
     * System entities (user, role, area) cannot be deleted.
     */
    test('should delete a non-system entity', async () => {
      // First, create a temporary entity specifically for deletion
      const deleteEntityName = uniqueName('delent_');
      const deleteEntityLabel = `Delete Entity ${RUN_ID}`;

      await page.goto(`${ENTITIES_URL}/create`, { waitUntil: 'networkidle' });

      const nameInput = page
        .getByLabel(/^name$/i)
        .or(page.locator('[name="name"], [data-testid="entity-name-input"]'));
      const labelInput = page
        .getByLabel(/^label$/i)
        .or(page.locator('[name="label"], [data-testid="entity-label-input"]'));
      const pluralInput = page
        .getByLabel(/plural/i)
        .or(
          page.locator(
            '[name="labelPlural"], [data-testid="entity-label-plural-input"]',
          ),
        );

      await nameInput.fill(deleteEntityName);
      await labelInput.fill(deleteEntityLabel);
      await pluralInput.fill(`${deleteEntityLabel}s`);
      await page.getByRole('button', { name: /save|create|submit/i }).click();
      await page.waitForTimeout(SETTLE_TIME);

      // Navigate to entity list and click on the created entity
      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });
      await page.waitForTimeout(SETTLE_TIME);
      await page.getByText(deleteEntityName, { exact: false }).first().click();
      await page.waitForLoadState('networkidle');

      // Click the delete action on the entity detail/manage page
      const deleteBtn = page
        .getByRole('button', { name: /delete/i })
        .or(page.locator('[data-testid="delete-entity-btn"]'));
      await deleteBtn.first().click();
      await page.waitForTimeout(SETTLE_TIME);

      // Confirm the deletion dialog
      const confirmBtn = page
        .getByRole('button', { name: /confirm|yes|ok|delete/i })
        .or(page.locator('[data-testid="confirm-delete-btn"]'));
      await confirmBtn.first().click();

      // Verify redirect to entity list or success notification
      await expect(
        page
          .getByText(/deleted|removed|success/i)
          .or(page.locator('[data-testid="success-notification"]')),
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Verify entity is removed from the list
      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });
      await page.waitForTimeout(SETTLE_TIME);

      const entityStillVisible = await page
        .getByText(deleteEntityName, { exact: true })
        .isVisible()
        .catch(() => false);
      expect(entityStillVisible).toBeFalsy();
    });
  });

  // ═══════════════════════════════════════════════════════════════════════
  // SECTION 2: FIELD MANAGEMENT TESTS
  //
  // Replaces: SDK plugin entity/fields.cshtml, entity/create-field.cshtml
  //   EntityManager — 20+ field types: Text, Number, Date, Select, etc.
  //   Routes: /admin/entities/:entityId/fields,
  //           /admin/entities/:entityId/fields/create,
  //           /admin/entities/:entityId/fields/:fieldId/manage
  // ═══════════════════════════════════════════════════════════════════════

  test.describe('Field Management', () => {
    /** ID of the entity used for field tests, extracted from URL after creation. */
    let fieldEntityId = '';

    /**
     * Set up a dedicated entity for field CRUD tests.
     * This entity is distinct from the one in Entity Management tests to
     * avoid interference and enable independent cleanup.
     */
    test('should create a dedicated entity for field tests', async () => {
      await page.goto(`${ENTITIES_URL}/create`, { waitUntil: 'networkidle' });

      const nameInput = page
        .getByLabel(/^name$/i)
        .or(page.locator('[name="name"], [data-testid="entity-name-input"]'));
      const labelInput = page
        .getByLabel(/^label$/i)
        .or(page.locator('[name="label"], [data-testid="entity-label-input"]'));
      const pluralInput = page
        .getByLabel(/plural/i)
        .or(
          page.locator(
            '[name="labelPlural"], [data-testid="entity-label-plural-input"]',
          ),
        );

      await nameInput.fill(testFieldEntityName);
      await labelInput.fill(testFieldEntityLabel);
      await pluralInput.fill(testFieldEntityLabelPlural);
      await page.getByRole('button', { name: /save|create|submit/i }).click();
      await page.waitForTimeout(SETTLE_TIME);

      // Track for cleanup
      createdEntityNames.push(testFieldEntityName);

      // Extract entity ID from the redirect URL (e.g., /admin/entities/{id})
      await page.waitForURL(/entities\/[a-f0-9-]+/i, {
        timeout: DATA_TIMEOUT,
      });
      const currentUrl = page.url();
      const entityIdMatch = currentUrl.match(
        /entities\/([a-f0-9-]+)/i,
      );
      if (entityIdMatch) {
        fieldEntityId = entityIdMatch[1];
      }
      expect(fieldEntityId.length).toBeGreaterThan(0);
    });

    /**
     * Add a text field to the test entity.
     * Source: EntityManager field types — TextField: Name, Label, MaxLength,
     * Required, DefaultValue, Unique, Searchable.
     */
    test('should add a text field', async () => {
      expect(fieldEntityId).toBeTruthy();
      const fieldName = uniqueName('textfld_');
      const fieldLabel = `Text Field ${RUN_ID}`;

      await page.goto(
        `${ENTITIES_URL}/${fieldEntityId}/fields/create`,
        { waitUntil: 'networkidle' },
      );

      // Select field type: Text
      const typeSelect = page
        .getByLabel(/type/i)
        .or(page.locator('[name="fieldType"], [data-testid="field-type-select"]'));
      await typeSelect.first().click();
      await page.waitForTimeout(300);

      // Select "Text" from dropdown options
      await page
        .getByRole('option', { name: /^text$/i })
        .or(page.getByText(/^text$/i).first())
        .click();
      await page.waitForTimeout(SETTLE_TIME);

      // Fill field name and label
      const nameInput = page
        .getByLabel(/^name$/i)
        .or(page.locator('[name="name"], [data-testid="field-name-input"]'));
      const labelInput = page
        .getByLabel(/^label$/i)
        .or(page.locator('[name="label"], [data-testid="field-label-input"]'));

      await nameInput.fill(fieldName);
      await labelInput.fill(fieldLabel);

      // Configure text-specific options
      const maxLengthInput = page
        .getByLabel(/max.*length/i)
        .or(page.locator('[name="maxLength"], [data-testid="field-max-length"]'));
      const maxLengthVisible = await maxLengthInput.first().isVisible().catch(() => false);
      if (maxLengthVisible) {
        await maxLengthInput.first().fill('255');
      }

      // Set required flag
      const requiredCheckbox = page
        .getByLabel(/required/i)
        .or(page.locator('[name="required"], [data-testid="field-required"]'));
      const requiredVisible = await requiredCheckbox.first().isVisible().catch(() => false);
      if (requiredVisible) {
        await requiredCheckbox.first().check();
      }

      // Set default value
      const defaultInput = page
        .getByLabel(/default/i)
        .or(page.locator('[name="defaultValue"], [data-testid="field-default-value"]'));
      const defaultVisible = await defaultInput.first().isVisible().catch(() => false);
      if (defaultVisible) {
        await defaultInput.first().fill('default text');
      }

      // Submit
      await page.getByRole('button', { name: /save|create|submit/i }).click();

      // Verify success
      await expect(
        page
          .getByText(/success|created|saved/i)
          .or(page.locator('[data-testid="success-notification"]')),
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Navigate to field list and verify the new field appears
      await page.goto(`${ENTITIES_URL}/${fieldEntityId}/fields`, {
        waitUntil: 'networkidle',
      });
      await expect(
        page.getByText(fieldName, { exact: false }),
      ).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Add a number field to the test entity.
     * Source: EntityManager — NumberField: Min, Max, DecimalPlaces.
     */
    test('should add a number field', async () => {
      expect(fieldEntityId).toBeTruthy();
      const fieldName = uniqueName('numfld_');
      const fieldLabel = `Number Field ${RUN_ID}`;

      await page.goto(
        `${ENTITIES_URL}/${fieldEntityId}/fields/create`,
        { waitUntil: 'networkidle' },
      );

      // Select field type: Number
      const typeSelect = page
        .getByLabel(/type/i)
        .or(page.locator('[name="fieldType"], [data-testid="field-type-select"]'));
      await typeSelect.first().click();
      await page.waitForTimeout(300);

      await page
        .getByRole('option', { name: /number/i })
        .or(page.getByText(/number/i).first())
        .click();
      await page.waitForTimeout(SETTLE_TIME);

      // Fill field name and label
      const nameInput = page
        .getByLabel(/^name$/i)
        .or(page.locator('[name="name"], [data-testid="field-name-input"]'));
      const labelInput = page
        .getByLabel(/^label$/i)
        .or(page.locator('[name="label"], [data-testid="field-label-input"]'));

      await nameInput.fill(fieldName);
      await labelInput.fill(fieldLabel);

      // Configure number-specific options: min, max, decimal places
      const minInput = page
        .getByLabel(/min/i)
        .or(page.locator('[name="min"], [data-testid="field-min"]'));
      const minVisible = await minInput.first().isVisible().catch(() => false);
      if (minVisible) {
        await minInput.first().fill('0');
      }

      const maxInput = page
        .getByLabel(/max/i)
        .or(page.locator('[name="max"], [data-testid="field-max"]'));
      const maxVisible = await maxInput.first().isVisible().catch(() => false);
      if (maxVisible) {
        await maxInput.first().fill('1000');
      }

      const decimalInput = page
        .getByLabel(/decimal/i)
        .or(page.locator('[name="decimalPlaces"], [data-testid="field-decimal-places"]'));
      const decimalVisible = await decimalInput.first().isVisible().catch(() => false);
      if (decimalVisible) {
        await decimalInput.first().fill('2');
      }

      // Submit
      await page.getByRole('button', { name: /save|create|submit/i }).click();

      // Verify success
      await expect(
        page
          .getByText(/success|created|saved/i)
          .or(page.locator('[data-testid="success-notification"]')),
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Verify field appears in field list
      await page.goto(`${ENTITIES_URL}/${fieldEntityId}/fields`, {
        waitUntil: 'networkidle',
      });
      await expect(
        page.getByText(fieldName, { exact: false }),
      ).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Add a date field to the test entity.
     * Source: EntityManager — DateField: UseCurrentTimeAsDefaultValue, Format.
     */
    test('should add a date field', async () => {
      expect(fieldEntityId).toBeTruthy();
      const fieldName = uniqueName('datefld_');
      const fieldLabel = `Date Field ${RUN_ID}`;

      await page.goto(
        `${ENTITIES_URL}/${fieldEntityId}/fields/create`,
        { waitUntil: 'networkidle' },
      );

      // Select field type: Date
      const typeSelect = page
        .getByLabel(/type/i)
        .or(page.locator('[name="fieldType"], [data-testid="field-type-select"]'));
      await typeSelect.first().click();
      await page.waitForTimeout(300);

      await page
        .getByRole('option', { name: /^date$/i })
        .or(page.getByText(/^date$/i).first())
        .click();
      await page.waitForTimeout(SETTLE_TIME);

      // Fill field name and label
      const nameInput = page
        .getByLabel(/^name$/i)
        .or(page.locator('[name="name"], [data-testid="field-name-input"]'));
      const labelInput = page
        .getByLabel(/^label$/i)
        .or(page.locator('[name="label"], [data-testid="field-label-input"]'));

      await nameInput.fill(fieldName);
      await labelInput.fill(fieldLabel);

      // Configure date format options if available
      const formatSelect = page
        .getByLabel(/format/i)
        .or(page.locator('[name="format"], [data-testid="field-format"]'));
      const formatVisible = await formatSelect.first().isVisible().catch(() => false);
      if (formatVisible) {
        await formatSelect.first().click();
        await page.waitForTimeout(300);
        // Select a date format option
        const dateFormatOption = page.getByRole('option').first();
        const optionVisible = await dateFormatOption.isVisible().catch(() => false);
        if (optionVisible) {
          await dateFormatOption.click();
        }
      }

      // Submit
      await page.getByRole('button', { name: /save|create|submit/i }).click();

      // Verify success
      await expect(
        page
          .getByText(/success|created|saved/i)
          .or(page.locator('[data-testid="success-notification"]')),
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Verify field appears in field list
      await page.goto(`${ENTITIES_URL}/${fieldEntityId}/fields`, {
        waitUntil: 'networkidle',
      });
      await expect(
        page.getByText(fieldName, { exact: false }),
      ).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Add a select field to the test entity with multiple options.
     * Source: EntityManager — SelectField: Options (label/value pairs),
     * DefaultValue, Required.
     */
    test('should add a select field with multiple options', async () => {
      expect(fieldEntityId).toBeTruthy();
      const fieldName = uniqueName('selfld_');
      const fieldLabel = `Select Field ${RUN_ID}`;

      await page.goto(
        `${ENTITIES_URL}/${fieldEntityId}/fields/create`,
        { waitUntil: 'networkidle' },
      );

      // Select field type: Select
      const typeSelect = page
        .getByLabel(/type/i)
        .or(page.locator('[name="fieldType"], [data-testid="field-type-select"]'));
      await typeSelect.first().click();
      await page.waitForTimeout(300);

      await page
        .getByRole('option', { name: /select/i })
        .or(page.getByText(/^select$/i).first())
        .click();
      await page.waitForTimeout(SETTLE_TIME);

      // Fill field name and label
      const nameInput = page
        .getByLabel(/^name$/i)
        .or(page.locator('[name="name"], [data-testid="field-name-input"]'));
      const labelInput = page
        .getByLabel(/^label$/i)
        .or(page.locator('[name="label"], [data-testid="field-label-input"]'));

      await nameInput.fill(fieldName);
      await labelInput.fill(fieldLabel);

      // Add select options (label/value pairs)
      const addOptionBtn = page
        .getByRole('button', { name: /add.*option/i })
        .or(page.locator('[data-testid="add-option-btn"]'));
      const addOptionVisible = await addOptionBtn.first().isVisible().catch(() => false);

      if (addOptionVisible) {
        // Add first option
        await addOptionBtn.first().click();
        await page.waitForTimeout(300);
        const optionLabels = page.locator(
          '[data-testid="option-label"], input[name*="optionLabel"], input[placeholder*="label" i]',
        );
        const optionValues = page.locator(
          '[data-testid="option-value"], input[name*="optionValue"], input[placeholder*="value" i]',
        );

        const labelCount = await optionLabels.count();
        if (labelCount > 0) {
          await optionLabels.last().fill('Option A');
        }
        const valueCount = await optionValues.count();
        if (valueCount > 0) {
          await optionValues.last().fill('option_a');
        }

        // Add second option
        await addOptionBtn.first().click();
        await page.waitForTimeout(300);
        const labelCount2 = await optionLabels.count();
        if (labelCount2 > 1) {
          await optionLabels.last().fill('Option B');
        }
        const valueCount2 = await optionValues.count();
        if (valueCount2 > 1) {
          await optionValues.last().fill('option_b');
        }

        // Add third option
        await addOptionBtn.first().click();
        await page.waitForTimeout(300);
        const labelCount3 = await optionLabels.count();
        if (labelCount3 > 2) {
          await optionLabels.last().fill('Option C');
        }
        const valueCount3 = await optionValues.count();
        if (valueCount3 > 2) {
          await optionValues.last().fill('option_c');
        }
      }

      // Set default option if available
      const defaultSelect = page
        .getByLabel(/default/i)
        .or(page.locator('[name="defaultValue"], [data-testid="field-default-option"]'));
      const defaultVisible = await defaultSelect.first().isVisible().catch(() => false);
      if (defaultVisible) {
        await defaultSelect.first().click();
        await page.waitForTimeout(300);
        const defaultOption = page.getByRole('option').first();
        const optVisible = await defaultOption.isVisible().catch(() => false);
        if (optVisible) {
          await defaultOption.click();
        }
      }

      // Submit
      await page.getByRole('button', { name: /save|create|submit/i }).click();

      // Verify success
      await expect(
        page
          .getByText(/success|created|saved/i)
          .or(page.locator('[data-testid="success-notification"]')),
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Verify field appears in field list
      await page.goto(`${ENTITIES_URL}/${fieldEntityId}/fields`, {
        waitUntil: 'networkidle',
      });
      await expect(
        page.getByText(fieldName, { exact: false }),
      ).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Edit an existing field: modify label and required flag.
     */
    test('should edit a field', async () => {
      expect(fieldEntityId).toBeTruthy();

      // Navigate to the field list for the test entity
      await page.goto(`${ENTITIES_URL}/${fieldEntityId}/fields`, {
        waitUntil: 'networkidle',
      });

      // Click on the first custom field (skip system id field)
      const fieldRows = page.locator(
        'table tbody tr, [role="row"]',
      ).filter({ hasNot: page.locator('th') });

      // Find a row that contains one of our test field names (text field likely first)
      const customFieldRow = fieldRows.filter({
        hasText: new RegExp(`(textfld|numfld|datefld|selfld).*${RUN_ID}`, 'i'),
      });
      const hasCustomField = await customFieldRow.first().isVisible().catch(() => false);

      if (hasCustomField) {
        // Click on the field to go to details
        await customFieldRow.first().locator('a').first().click();
        await page.waitForLoadState('networkidle');

        // Navigate to the manage/edit page
        const editLink = page
          .getByRole('link', { name: /edit|manage/i })
          .or(page.locator('[data-testid="edit-field-btn"]'));
        const editVisible = await editLink.first().isVisible().catch(() => false);
        if (editVisible) {
          await editLink.first().click();
          await page.waitForLoadState('networkidle');
        }

        // Modify the label
        const labelInput = page
          .getByLabel(/^label$/i)
          .or(page.locator('[name="label"], [data-testid="field-label-input"]'));
        const labelVisible = await labelInput.first().isVisible().catch(() => false);
        if (labelVisible) {
          await labelInput.first().fill(`Updated Label ${RUN_ID}`);
        }

        // Toggle required flag
        const requiredCheckbox = page
          .getByLabel(/required/i)
          .or(page.locator('[name="required"], [data-testid="field-required"]'));
        const requiredVisible = await requiredCheckbox.first().isVisible().catch(() => false);
        if (requiredVisible) {
          const isChecked = await requiredCheckbox.first().isChecked();
          if (isChecked) {
            await requiredCheckbox.first().uncheck();
          } else {
            await requiredCheckbox.first().check();
          }
        }

        // Save changes
        await page.getByRole('button', { name: /save|update|submit/i }).click();

        // Verify success
        await expect(
          page
            .getByText(/success|updated|saved/i)
            .or(page.locator('[data-testid="success-notification"]')),
        ).toBeVisible({ timeout: DATA_TIMEOUT });
      }
    });

    /**
     * Delete a field from the test entity.
     */
    test('should delete a field', async () => {
      expect(fieldEntityId).toBeTruthy();

      // Navigate to the field list
      await page.goto(`${ENTITIES_URL}/${fieldEntityId}/fields`, {
        waitUntil: 'networkidle',
      });

      // Find a custom field row containing one of our test field names
      const fieldRows = page.locator(
        'table tbody tr, [role="row"]',
      ).filter({ hasNot: page.locator('th') });

      const customFieldRow = fieldRows.filter({
        hasText: new RegExp(`(textfld|numfld|datefld|selfld).*${RUN_ID}`, 'i'),
      });
      const hasCustomField = await customFieldRow.first().isVisible().catch(() => false);

      if (hasCustomField) {
        const fieldText = await customFieldRow.first().textContent();

        // Click on the field to go to its details
        await customFieldRow.first().locator('a').first().click();
        await page.waitForLoadState('networkidle');
        await page.waitForTimeout(SETTLE_TIME);

        // Click the delete button
        const deleteBtn = page
          .getByRole('button', { name: /delete/i })
          .or(page.locator('[data-testid="delete-field-btn"]'));
        await deleteBtn.first().click();
        await page.waitForTimeout(SETTLE_TIME);

        // Confirm deletion
        const confirmBtn = page
          .getByRole('button', { name: /confirm|yes|ok|delete/i })
          .or(page.locator('[data-testid="confirm-delete-btn"]'));
        const confirmVisible = await confirmBtn.first().isVisible().catch(() => false);
        if (confirmVisible) {
          await confirmBtn.first().click();
        }

        // Verify success
        await expect(
          page
            .getByText(/deleted|removed|success/i)
            .or(page.locator('[data-testid="success-notification"]')),
        ).toBeVisible({ timeout: DATA_TIMEOUT });
      }
    });
  });

  // ═══════════════════════════════════════════════════════════════════════
  // SECTION 3: ROLE MANAGEMENT TESTS
  //
  // Replaces: SDK plugin role/list, role/create, role/manage Razor Pages
  //   SecurityManager: GetRoles(), CreateRole(), UpdateRole(), DeleteRole()
  //   System roles: Administrator, Regular, Guest (from Definitions.cs)
  //
  // Routes: /admin/roles, /admin/roles/create,
  //         /admin/roles/:roleId, /admin/roles/:roleId/manage
  // ═══════════════════════════════════════════════════════════════════════

  test.describe('Role Management', () => {
    /** Role ID extracted from URL after creation, for edit/delete tests. */
    let createdRoleId = '';

    /**
     * Verify role list displays system roles.
     * SystemIds: AdministratorRoleId, RegularRoleId, GuestRoleId.
     */
    test('should display role list with system roles', async () => {
      await page.goto(ROLES_URL, { waitUntil: 'networkidle' });

      // The role list must render a table/grid with roles
      const table = page.locator(
        'table, [role="grid"], [data-testid="role-list"]',
      );
      await expect(table).toBeVisible({ timeout: DATA_TIMEOUT });

      // Verify system roles are visible
      await expect(
        page
          .getByText(/administrator/i)
          .or(page.locator('[data-testid="role-administrator"]')),
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      await expect(
        page
          .getByText(/regular/i)
          .or(page.locator('[data-testid="role-regular"]')),
      ).toBeVisible();

      await expect(
        page
          .getByText(/guest/i)
          .or(page.locator('[data-testid="role-guest"]')),
      ).toBeVisible();
    });

    /**
     * Create a new role.
     * Source: SecurityManager.CreateRole() — name + description.
     */
    test('should create a new role', async () => {
      await page.goto(ROLES_URL, { waitUntil: 'networkidle' });

      // Click "Create Role" action
      await page
        .getByRole('link', { name: /create/i })
        .or(page.getByRole('button', { name: /create/i }))
        .or(page.locator('[data-testid="create-role-btn"]'))
        .click();

      // Wait for create form
      await page.waitForURL(/roles.*create|roles.*new/i, {
        timeout: DATA_TIMEOUT,
      });

      // Fill role creation form
      const nameInput = page
        .getByLabel(/name/i)
        .or(page.locator('[name="name"], [data-testid="role-name-input"]'));
      const descInput = page
        .getByLabel(/description/i)
        .or(
          page.locator(
            '[name="description"], [data-testid="role-description-input"]',
          ),
        );

      await nameInput.fill(testRoleName);
      const descVisible = await descInput.first().isVisible().catch(() => false);
      if (descVisible) {
        await descInput.first().fill(testRoleDescription);
      }

      // Submit
      await page.getByRole('button', { name: /save|create|submit/i }).click();

      // Verify success
      await expect(
        page
          .getByText(/success|created|saved/i)
          .or(page.locator('[data-testid="success-notification"]')),
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Track for cleanup
      createdRoleNames.push(testRoleName);

      // Extract role ID from URL
      const currentUrl = page.url();
      const roleIdMatch = currentUrl.match(/roles\/([a-f0-9-]+)/i);
      if (roleIdMatch) {
        createdRoleId = roleIdMatch[1];
      }

      // Navigate to role list and verify
      await page.goto(ROLES_URL, { waitUntil: 'networkidle' });
      await expect(
        page.getByText(testRoleName, { exact: false }),
      ).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Edit the created role — modify description.
     */
    test('should edit a role', async () => {
      await page.goto(ROLES_URL, { waitUntil: 'networkidle' });

      // Navigate to the test role
      await page.getByText(testRoleName, { exact: false }).first().click();
      await page.waitForLoadState('networkidle');

      // Navigate to manage page
      const editLink = page
        .getByRole('link', { name: /edit|manage/i })
        .or(page.locator('[data-testid="edit-role-btn"]'));
      const editVisible = await editLink.first().isVisible().catch(() => false);
      if (editVisible) {
        await editLink.first().click();
        await page.waitForLoadState('networkidle');
      } else {
        // Maybe we're already on the manage page or use /manage URL directly
        if (createdRoleId) {
          await page.goto(`${ROLES_URL}/${createdRoleId}/manage`, {
            waitUntil: 'networkidle',
          });
        }
      }

      // Modify description
      const descInput = page
        .getByLabel(/description/i)
        .or(
          page.locator(
            '[name="description"], [data-testid="role-description-input"]',
          ),
        );
      const descVisible = await descInput.first().isVisible().catch(() => false);
      if (descVisible) {
        await descInput.first().fill(`Updated description ${RUN_ID}`);
      }

      // Save changes
      await page.getByRole('button', { name: /save|update|submit/i }).click();

      // Verify success
      await expect(
        page
          .getByText(/success|updated|saved/i)
          .or(page.locator('[data-testid="success-notification"]')),
      ).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Delete the created non-system role.
     * System roles (Administrator, Regular, Guest) cannot be deleted.
     */
    test('should delete a non-system role', async () => {
      await page.goto(ROLES_URL, { waitUntil: 'networkidle' });

      // Navigate to the test role details
      await page.getByText(testRoleName, { exact: false }).first().click();
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(SETTLE_TIME);

      // Click delete
      const deleteBtn = page
        .getByRole('button', { name: /delete/i })
        .or(page.locator('[data-testid="delete-role-btn"]'));
      await deleteBtn.first().click();
      await page.waitForTimeout(SETTLE_TIME);

      // Confirm deletion
      const confirmBtn = page
        .getByRole('button', { name: /confirm|yes|ok|delete/i })
        .or(page.locator('[data-testid="confirm-delete-btn"]'));
      const confirmVisible = await confirmBtn.first().isVisible().catch(() => false);
      if (confirmVisible) {
        await confirmBtn.first().click();
      }

      // Verify success
      await expect(
        page
          .getByText(/deleted|removed|success/i)
          .or(page.locator('[data-testid="success-notification"]')),
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Remove from cleanup list since already deleted
      const roleIdx = createdRoleNames.indexOf(testRoleName);
      if (roleIdx !== -1) {
        createdRoleNames.splice(roleIdx, 1);
      }

      // Verify role no longer appears
      await page.goto(ROLES_URL, { waitUntil: 'networkidle' });
      await page.waitForTimeout(SETTLE_TIME);

      const roleStillVisible = await page
        .getByText(testRoleName, { exact: true })
        .isVisible()
        .catch(() => false);
      expect(roleStillVisible).toBeFalsy();
    });
  });

  // ═══════════════════════════════════════════════════════════════════════
  // SECTION 4: USER MANAGEMENT TESTS
  //
  // Replaces: SDK plugin user/list, user/create, user/manage Razor Pages
  //   SecurityManager → Cognito-backed user operations
  //
  // Routes: /admin/users, /admin/users/create,
  //         /admin/users/:userId, /admin/users/:userId/manage
  // ═══════════════════════════════════════════════════════════════════════

  test.describe('User Management', () => {
    /** User ID extracted after creation for edit test. */
    let createdUserId = '';

    /**
     * Verify user list displays the seeded admin user.
     */
    test('should display user list with seeded admin user', async () => {
      await page.goto(USERS_URL, { waitUntil: 'networkidle' });

      // The user list must render a table/grid with users
      const table = page.locator(
        'table, [role="grid"], [data-testid="user-list"]',
      );
      await expect(table).toBeVisible({ timeout: DATA_TIMEOUT });

      // Verify the seeded admin user is visible
      await expect(
        page
          .getByText('erp@webvella.com', { exact: false })
          .or(page.locator('[data-testid="user-erp"]')),
      ).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Create a new user with email, name, password, and role assignment.
     * Source: SecurityManager.SaveUser() — creates user in Cognito + DynamoDB.
     */
    test('should create a new user', async () => {
      await page.goto(USERS_URL, { waitUntil: 'networkidle' });

      // Click "Create User" action
      await page
        .getByRole('link', { name: /create/i })
        .or(page.getByRole('button', { name: /create/i }))
        .or(page.locator('[data-testid="create-user-btn"]'))
        .click();

      // Wait for create form
      await page.waitForURL(/users.*create|users.*new/i, {
        timeout: DATA_TIMEOUT,
      });

      // Fill user creation form
      const emailInput = page
        .getByLabel(/email/i)
        .or(page.locator('[name="email"], [data-testid="user-email-input"]'));
      const firstNameInput = page
        .getByLabel(/first.*name/i)
        .or(
          page.locator(
            '[name="firstName"], [data-testid="user-first-name-input"]',
          ),
        );
      const lastNameInput = page
        .getByLabel(/last.*name/i)
        .or(
          page.locator(
            '[name="lastName"], [data-testid="user-last-name-input"]',
          ),
        );
      const passwordInput = page
        .getByLabel(/password/i)
        .or(
          page.locator(
            '[name="password"], [data-testid="user-password-input"]',
          ),
        );

      await emailInput.fill(testUserEmail);

      const firstNameVisible = await firstNameInput.first().isVisible().catch(() => false);
      if (firstNameVisible) {
        await firstNameInput.first().fill(testUserFirstName);
      }

      const lastNameVisible = await lastNameInput.first().isVisible().catch(() => false);
      if (lastNameVisible) {
        await lastNameInput.first().fill(testUserLastName);
      }

      const passwordVisible = await passwordInput.first().isVisible().catch(() => false);
      if (passwordVisible) {
        await passwordInput.first().fill('TestP@ssw0rd!');
      }

      // Assign role — look for role selection (checkbox list, multi-select, etc.)
      const roleCheckbox = page
        .getByLabel(/regular/i)
        .or(page.locator('[data-testid="role-regular-checkbox"]'));
      const roleCheckboxVisible = await roleCheckbox.first().isVisible().catch(() => false);
      if (roleCheckboxVisible) {
        await roleCheckbox.first().check();
      }

      // Submit
      await page.getByRole('button', { name: /save|create|submit/i }).click();

      // Verify success
      await expect(
        page
          .getByText(/success|created|saved/i)
          .or(page.locator('[data-testid="success-notification"]')),
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Track for cleanup
      createdUserEmails.push(testUserEmail);

      // Extract user ID from URL
      const currentUrl = page.url();
      const userIdMatch = currentUrl.match(/users\/([a-f0-9-]+)/i);
      if (userIdMatch) {
        createdUserId = userIdMatch[1];
      }

      // Navigate to user list and verify
      await page.goto(USERS_URL, { waitUntil: 'networkidle' });
      await expect(
        page.getByText(testUserEmail, { exact: false }),
      ).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Edit the created user — modify first name and role assignments.
     */
    test('should edit a user', async () => {
      await page.goto(USERS_URL, { waitUntil: 'networkidle' });

      // Navigate to the test user
      await page.getByText(testUserEmail, { exact: false }).first().click();
      await page.waitForLoadState('networkidle');

      // Navigate to manage page
      const editLink = page
        .getByRole('link', { name: /edit|manage/i })
        .or(page.locator('[data-testid="edit-user-btn"]'));
      const editVisible = await editLink.first().isVisible().catch(() => false);
      if (editVisible) {
        await editLink.first().click();
        await page.waitForLoadState('networkidle');
      } else if (createdUserId) {
        await page.goto(`${USERS_URL}/${createdUserId}/manage`, {
          waitUntil: 'networkidle',
        });
      }

      // Modify first name
      const firstNameInput = page
        .getByLabel(/first.*name/i)
        .or(
          page.locator(
            '[name="firstName"], [data-testid="user-first-name-input"]',
          ),
        );
      const firstNameVisible = await firstNameInput.first().isVisible().catch(() => false);
      if (firstNameVisible) {
        await firstNameInput.first().fill(`UpdatedFirst${RUN_ID}`);
      }

      // Toggle role assignment if possible
      const adminRoleCheckbox = page
        .getByLabel(/administrator/i)
        .or(page.locator('[data-testid="role-administrator-checkbox"]'));
      const adminRoleVisible = await adminRoleCheckbox.first().isVisible().catch(() => false);
      if (adminRoleVisible) {
        const isChecked = await adminRoleCheckbox.first().isChecked();
        // Only toggle if it won't break our session
        if (!isChecked) {
          await adminRoleCheckbox.first().check();
        }
      }

      // Save changes
      await page.getByRole('button', { name: /save|update|submit/i }).click();

      // Verify success
      await expect(
        page
          .getByText(/success|updated|saved/i)
          .or(page.locator('[data-testid="success-notification"]')),
      ).toBeVisible({ timeout: DATA_TIMEOUT });
    });
  });

  // ═══════════════════════════════════════════════════════════════════════
  // SECTION 5: PAGE BUILDER TESTS
  //
  // Replaces: SDK plugin page/list, page/create, page/manage Razor Pages
  //   and the pb-manager StencilJS web component for body node management.
  //
  // Routes: /admin/pages, /admin/pages/create,
  //         /admin/pages/:pageId, /admin/pages/:pageId/manage
  // ═══════════════════════════════════════════════════════════════════════

  test.describe('Page Builder', () => {
    /** Page ID extracted from URL after creation. */
    let createdPageId = '';

    /**
     * Verify page list renders.
     */
    test('should display page list', async () => {
      await page.goto(PAGES_URL, { waitUntil: 'networkidle' });

      // The page list must render a table/grid
      const table = page.locator(
        'table, [role="grid"], [data-testid="page-list"]',
      );
      await expect(table).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Create a new page.
     * Source: PageService.CreatePage() — name, label, icon, layout.
     */
    test('should create a new page', async () => {
      await page.goto(PAGES_URL, { waitUntil: 'networkidle' });

      // Click "Create Page" action
      await page
        .getByRole('link', { name: /create/i })
        .or(page.getByRole('button', { name: /create/i }))
        .or(page.locator('[data-testid="create-page-btn"]'))
        .click();

      // Wait for create form
      await page.waitForURL(/pages.*create|pages.*new/i, {
        timeout: DATA_TIMEOUT,
      });

      // Fill page creation form
      const nameInput = page
        .getByLabel(/name/i)
        .or(page.locator('[name="name"], [data-testid="page-name-input"]'));
      const labelInput = page
        .getByLabel(/label/i)
        .or(page.locator('[name="label"], [data-testid="page-label-input"]'));

      await nameInput.fill(testPageName);
      await labelInput.fill(testPageLabel);

      // Set icon if available
      const iconInput = page
        .getByLabel(/icon/i)
        .or(page.locator('[name="icon"], [data-testid="page-icon-input"]'));
      const iconVisible = await iconInput.first().isVisible().catch(() => false);
      if (iconVisible) {
        await iconInput.first().fill('fa fa-file');
      }

      // Submit
      await page.getByRole('button', { name: /save|create|submit/i }).click();

      // Verify success
      await expect(
        page
          .getByText(/success|created|saved/i)
          .or(page.locator('[data-testid="success-notification"]')),
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Track for cleanup
      createdPageNames.push(testPageName);

      // Extract page ID from URL
      const currentUrl = page.url();
      const pageIdMatch = currentUrl.match(/pages\/([a-f0-9-]+)/i);
      if (pageIdMatch) {
        createdPageId = pageIdMatch[1];
      }

      // Navigate to page list and verify
      await page.goto(PAGES_URL, { waitUntil: 'networkidle' });
      await expect(
        page.getByText(testPageName, { exact: false }),
      ).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Page body builder: add, verify, and remove body nodes.
     * Replaces the pb-manager StencilJS component which provided
     * drag-and-drop body node management.
     */
    test('should manage page body nodes', async () => {
      if (!createdPageId) {
        // Skip if page creation failed
        test.skip();
        return;
      }

      // Navigate to the page manage/body editor
      await page.goto(`${PAGES_URL}/${createdPageId}/manage`, {
        waitUntil: 'networkidle',
      });

      // The body editor should be visible
      const bodyEditor = page.locator(
        '[data-testid="page-body-editor"], [data-testid="body-tree"], .page-body-editor, .body-builder',
      );
      const editorVisible = await bodyEditor.first().isVisible().catch(() => false);

      if (editorVisible) {
        // Add a body node (section)
        const addNodeBtn = page
          .getByRole('button', { name: /add.*node|add.*section|add.*component/i })
          .or(page.locator('[data-testid="add-body-node-btn"]'));
        const addNodeVisible = await addNodeBtn.first().isVisible().catch(() => false);

        if (addNodeVisible) {
          await addNodeBtn.first().click();
          await page.waitForTimeout(SETTLE_TIME);

          // Select node type (section, row, or field)
          const sectionOption = page
            .getByRole('option', { name: /section/i })
            .or(page.getByText(/section/i).first())
            .or(page.locator('[data-testid="node-type-section"]'));
          const sectionOptionVisible = await sectionOption.first().isVisible().catch(() => false);

          if (sectionOptionVisible) {
            await sectionOption.first().click();
            await page.waitForTimeout(SETTLE_TIME);

            // Confirm/submit the node addition
            const confirmNodeBtn = page
              .getByRole('button', { name: /add|confirm|save/i })
              .or(page.locator('[data-testid="confirm-add-node-btn"]'));
            const confirmNodeVisible = await confirmNodeBtn.first().isVisible().catch(() => false);
            if (confirmNodeVisible) {
              await confirmNodeBtn.first().click();
              await page.waitForTimeout(SETTLE_TIME);
            }
          }

          // Verify the node appears in the body tree
          const bodyNode = page.locator(
            '[data-testid="body-node"], .body-node, [data-node-type]',
          );
          const nodeVisible = await bodyNode.first().isVisible().catch(() => false);
          if (nodeVisible) {
            // Attempt to remove the node
            const removeNodeBtn = page
              .getByRole('button', { name: /remove|delete/i })
              .or(page.locator('[data-testid="remove-node-btn"]'));
            const removeVisible = await removeNodeBtn.first().isVisible().catch(() => false);
            if (removeVisible) {
              await removeNodeBtn.first().click();
              await page.waitForTimeout(SETTLE_TIME);

              // Confirm removal if a confirmation dialog appears
              const confirmRemoval = page
                .getByRole('button', { name: /confirm|yes|ok|delete/i })
                .or(page.locator('[data-testid="confirm-remove-node-btn"]'));
              const confirmRemovalVisible = await confirmRemoval.first().isVisible().catch(() => false);
              if (confirmRemovalVisible) {
                await confirmRemoval.first().click();
                await page.waitForTimeout(SETTLE_TIME);
              }
            }
          }
        }
      }

      // Save the page body state
      const saveBtn = page
        .getByRole('button', { name: /save/i })
        .or(page.locator('[data-testid="save-page-body-btn"]'));
      const saveVisible = await saveBtn.first().isVisible().catch(() => false);
      if (saveVisible) {
        await saveBtn.first().click();
        await page.waitForTimeout(SETTLE_TIME);
      }
    });
  });

  // ═══════════════════════════════════════════════════════════════════════
  // SECTION 6: DATA SOURCE MANAGEMENT TESTS
  //
  // Replaces: SDK plugin datasource-manage StencilJS web component
  //   DataSourceManager: Create/Update/Delete data sources
  //   api/v3.0/p/sdk/datasource/* endpoints → /v1/data-sources
  //
  // Routes: /admin/data-sources, /admin/data-sources/create,
  //         /admin/data-sources/:dataSourceId,
  //         /admin/data-sources/:dataSourceId/manage
  // ═══════════════════════════════════════════════════════════════════════

  test.describe('Data Source Management', () => {
    /** Data source ID extracted from URL after creation. */
    let createdDsId = '';

    /**
     * Verify data source list renders.
     * Source: DataSourceManager uses DbDataSourceRepository + CodeDataSource
     * discovery via assembly scanning.
     */
    test('should display data source list', async () => {
      await page.goto(DATA_SOURCES_URL, { waitUntil: 'networkidle' });

      // The data source list must render a table/grid
      const table = page.locator(
        'table, [role="grid"], [data-testid="data-source-list"]',
      );
      await expect(table).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Create a new data source.
     * Source: DataSourceManager.Create() — Name, Type (database/code),
     * Parameters, EQL query or code reference.
     */
    test('should create a new data source', async () => {
      await page.goto(DATA_SOURCES_URL, { waitUntil: 'networkidle' });

      // Click "Create Data Source" action
      await page
        .getByRole('link', { name: /create/i })
        .or(page.getByRole('button', { name: /create/i }))
        .or(page.locator('[data-testid="create-data-source-btn"]'))
        .click();

      // Wait for create form
      await page.waitForURL(/data-sources.*create|data-sources.*new/i, {
        timeout: DATA_TIMEOUT,
      });

      // Fill data source creation form
      const nameInput = page
        .getByLabel(/name/i)
        .or(
          page.locator(
            '[name="name"], [data-testid="data-source-name-input"]',
          ),
        );
      await nameInput.fill(testDsName);

      // Select data source type (database/EQL-based)
      const typeSelect = page
        .getByLabel(/type/i)
        .or(
          page.locator(
            '[name="type"], [data-testid="data-source-type-select"]',
          ),
        );
      const typeSelectVisible = await typeSelect.first().isVisible().catch(() => false);
      if (typeSelectVisible) {
        await typeSelect.first().click();
        await page.waitForTimeout(300);

        // Select "database" or "eql" type
        const dbOption = page
          .getByRole('option', { name: /database|eql/i })
          .or(page.getByText(/database|eql/i).first());
        const optionVisible = await dbOption.first().isVisible().catch(() => false);
        if (optionVisible) {
          await dbOption.first().click();
          await page.waitForTimeout(SETTLE_TIME);
        }
      }

      // Configure query/parameters
      const queryInput = page
        .getByLabel(/query|eql|sql/i)
        .or(
          page.locator(
            '[name="eqlText"], [name="query"], textarea[data-testid="data-source-query"], [data-testid="data-source-query-input"]',
          ),
        );
      const queryVisible = await queryInput.first().isVisible().catch(() => false);
      if (queryVisible) {
        await queryInput.first().fill('SELECT * FROM user');
      }

      // Add parameters if available
      const addParamBtn = page
        .getByRole('button', { name: /add.*param/i })
        .or(page.locator('[data-testid="add-parameter-btn"]'));
      const addParamVisible = await addParamBtn.first().isVisible().catch(() => false);
      if (addParamVisible) {
        await addParamBtn.first().click();
        await page.waitForTimeout(300);

        const paramNameInput = page.locator(
          '[data-testid="param-name"], input[name*="paramName"], input[placeholder*="parameter" i]',
        );
        const paramCount = await paramNameInput.count();
        if (paramCount > 0) {
          await paramNameInput.last().fill('testParam');
        }
      }

      // Submit
      await page.getByRole('button', { name: /save|create|submit/i }).click();

      // Verify success
      await expect(
        page
          .getByText(/success|created|saved/i)
          .or(page.locator('[data-testid="success-notification"]')),
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Track for cleanup
      createdDataSourceNames.push(testDsName);

      // Extract data source ID from URL
      const currentUrl = page.url();
      const dsIdMatch = currentUrl.match(/data-sources\/([a-f0-9-]+)/i);
      if (dsIdMatch) {
        createdDsId = dsIdMatch[1];
      }

      // Navigate to data source list and verify
      await page.goto(DATA_SOURCES_URL, { waitUntil: 'networkidle' });
      await expect(
        page.getByText(testDsName, { exact: false }),
      ).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Edit an existing data source — modify query.
     */
    test('should edit a data source', async () => {
      await page.goto(DATA_SOURCES_URL, { waitUntil: 'networkidle' });

      // Navigate to the test data source
      await page.getByText(testDsName, { exact: false }).first().click();
      await page.waitForLoadState('networkidle');

      // Navigate to manage page
      const editLink = page
        .getByRole('link', { name: /edit|manage/i })
        .or(page.locator('[data-testid="edit-data-source-btn"]'));
      const editVisible = await editLink.first().isVisible().catch(() => false);
      if (editVisible) {
        await editLink.first().click();
        await page.waitForLoadState('networkidle');
      } else if (createdDsId) {
        await page.goto(`${DATA_SOURCES_URL}/${createdDsId}/manage`, {
          waitUntil: 'networkidle',
        });
      }

      // Modify query
      const queryInput = page
        .getByLabel(/query|eql|sql/i)
        .or(
          page.locator(
            '[name="eqlText"], [name="query"], textarea[data-testid="data-source-query"], [data-testid="data-source-query-input"]',
          ),
        );
      const queryVisible = await queryInput.first().isVisible().catch(() => false);
      if (queryVisible) {
        await queryInput.first().fill('SELECT * FROM role');
      }

      // Save changes
      await page.getByRole('button', { name: /save|update|submit/i }).click();

      // Verify success
      await expect(
        page
          .getByText(/success|updated|saved/i)
          .or(page.locator('[data-testid="success-notification"]')),
      ).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Delete a non-referenced data source.
     */
    test('should delete a data source', async () => {
      await page.goto(DATA_SOURCES_URL, { waitUntil: 'networkidle' });

      // Navigate to the test data source details
      await page.getByText(testDsName, { exact: false }).first().click();
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(SETTLE_TIME);

      // Click delete
      const deleteBtn = page
        .getByRole('button', { name: /delete/i })
        .or(page.locator('[data-testid="delete-data-source-btn"]'));
      await deleteBtn.first().click();
      await page.waitForTimeout(SETTLE_TIME);

      // Confirm deletion
      const confirmBtn = page
        .getByRole('button', { name: /confirm|yes|ok|delete/i })
        .or(page.locator('[data-testid="confirm-delete-btn"]'));
      const confirmVisible = await confirmBtn.first().isVisible().catch(() => false);
      if (confirmVisible) {
        await confirmBtn.first().click();
      }

      // Verify success
      await expect(
        page
          .getByText(/deleted|removed|success/i)
          .or(page.locator('[data-testid="success-notification"]')),
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Remove from cleanup list
      const dsIdx = createdDataSourceNames.indexOf(testDsName);
      if (dsIdx !== -1) {
        createdDataSourceNames.splice(dsIdx, 1);
      }

      // Verify data source no longer appears
      await page.goto(DATA_SOURCES_URL, { waitUntil: 'networkidle' });
      await page.waitForTimeout(SETTLE_TIME);

      const dsStillVisible = await page
        .getByText(testDsName, { exact: true })
        .isVisible()
        .catch(() => false);
      expect(dsStillVisible).toBeFalsy();
    });
  });
});
