/**
 * Admin Console E2E Test Suite — WebVella ERP React SPA
 *
 * Validates all critical SDK admin console user-facing workflows against a full
 * LocalStack stack (API Gateway + Lambda + DynamoDB + Cognito).  Replaces the
 * monolith's WebVella.Erp.Plugins.SDK admin Razor Pages:
 *
 *   entity/list.cshtml.cs           — Entity listing with paging/filtering
 *   entity/create.cshtml.cs         — Entity creation (name, label, icon, color, permissions)
 *   entity/details.cshtml.cs        — Entity detail view with fields/relations counts
 *   entity/fields.cshtml.cs         — Field listing per entity
 *   entity/create-field.cshtml.cs   — Field creation (20+ field types)
 *   entity/relations.cshtml.cs      — Relation listing per entity
 *   role/list.cshtml.cs             — Role listing via EQL
 *   role/create.cshtml.cs           — Role creation (name + description)
 *   role/manage.cshtml.cs           — Role editing
 *   user/list.cshtml.cs             — User listing with role relation
 *   user/create.cshtml.cs           — User creation (Cognito-backed)
 *   user/manage.cshtml.cs           — User editing
 *
 * API mapping:
 *   entity/* pages → Entity Management service  /v1/entities
 *   role/*   pages → Identity service            /v1/roles
 *   user/*   pages → Identity service            /v1/users (Cognito)
 *
 * Test user: erp@webvella.com / erp (admin, seeded via seed-test-data.sh)
 *
 * Critical rules (AAP §0.8.1, §0.8.4):
 *   - ALL tests run against LocalStack — zero mocked AWS SDK calls.
 *   - Admin user must have the AdministratorRoleId-equivalent Cognito group.
 *   - Full behavioral parity for every SDK admin CRUD workflow.
 */

import { test, expect, Page } from '@playwright/test';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Admin user email — system administrator seeded into Cognito. */
const ADMIN_EMAIL: string = process.env.ADMIN_EMAIL ?? 'erp@webvella.com';

/** Admin user password — seeded via seed-test-data.sh (AAP §0.7.5). */
const ADMIN_PASSWORD: string = process.env.ADMIN_PASSWORD ?? 'erp';

/** Admin section root route — replaces /sdk/ route prefix in the monolith. */
const ADMIN_URL = '/admin';

/** Entity management page route. */
const ENTITIES_URL = `${ADMIN_URL}/entities`;

/** Role management page route. */
const ROLES_URL = `${ADMIN_URL}/roles`;

/** User management page route. */
const USERS_URL = `${ADMIN_URL}/users`;

/** Maximum time (ms) to wait for Cognito-backed admin auth to complete. */
const AUTH_TIMEOUT = 15_000;

/** Maximum time (ms) to wait for API-backed data rendering. */
const DATA_TIMEOUT = 10_000;

/**
 * Unique suffix for test entities/roles/users created during the test run.
 * Prevents collisions across parallel runs and makes cleanup deterministic.
 */
const RUN_ID = `e2e${Date.now().toString(36)}`;

// ---------------------------------------------------------------------------
// Reusable helpers
// ---------------------------------------------------------------------------

/**
 * Authenticates the admin user via the browser login form and navigates
 * to the admin section.  Mirrors the monolith's SDK plugin which required
 * the AdministratorRoleId for access to /sdk/* routes.
 *
 * @param page     Playwright Page instance.
 * @param email    Admin email address.
 * @param password Admin password.
 */
async function adminLogin(
  page: Page,
  email: string = ADMIN_EMAIL,
  password: string = ADMIN_PASSWORD,
): Promise<void> {
  await page.goto('/login', { waitUntil: 'networkidle' });

  const emailField = page.getByLabel(/email/i);
  const passwordField = page.getByLabel(/password/i);

  await emailField.fill(email);
  await passwordField.fill(password);

  await page.getByRole('button', { name: /login/i }).click();

  // Wait until we leave the login page (redirect to dashboard).
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), {
    timeout: AUTH_TIMEOUT,
  });
}

/**
 * Generates a unique lowercase alphanumeric name safe for entity/role/user
 * creation.  The monolith's EntityManager required names to be lowercase
 * with no spaces; this helper guarantees that constraint.
 *
 * @param prefix  Short human-readable prefix (e.g., "testent", "testrole").
 * @returns       A unique, lowercase, no-space string.
 */
function uniqueName(prefix: string): string {
  return `${prefix}${RUN_ID}`.toLowerCase().replace(/[^a-z0-9_]/g, '');
}

// ---------------------------------------------------------------------------
// Test Suite
// ---------------------------------------------------------------------------

test.describe('Admin Console', () => {
  // -----------------------------------------------------------------------
  // Lifecycle hooks
  // -----------------------------------------------------------------------

  /**
   * Before each test: authenticate as admin user and navigate to /admin.
   * The monolith required the SDK plugin's admin role; the React SPA
   * requires the administrator Cognito group for the /admin routes.
   */
  test.beforeEach(async ({ page }) => {
    await adminLogin(page);
    await page.goto(ADMIN_URL, { waitUntil: 'networkidle' });
  });

  /**
   * After each test: clear cookies and local storage to prevent cross-test
   * auth state leakage.
   */
  test.afterEach(async ({ context }) => {
    await context.clearCookies();
    const pages = context.pages();
    for (const p of pages) {
      try {
        await p.evaluate(() => {
          try { localStorage.clear(); } catch { /* safe */ }
          try { sessionStorage.clear(); } catch { /* safe */ }
        });
      } catch {
        // Page may not yet have a valid origin — ignore safely
      }
    }
  });

  // =======================================================================
  // ENTITY MANAGEMENT TESTS
  // Replaces entity/list, entity/create, entity/details, entity/manage
  // =======================================================================

  test.describe('Entity Management', () => {
    /**
     * Verify entity list renders with correct columns.
     * Source: entity/list.cshtml.cs — EntityManager.ReadEntities(), paging=15,
     * grid columns: action, name (sortable, default asc), label (sortable).
     */
    test('should display entity list', async ({ page }) => {
      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });

      // The entity list page must render a table/grid with entities.
      const table = page.locator('table, [role="grid"], [data-testid="entity-list"]');
      await expect(table).toBeVisible({ timeout: DATA_TIMEOUT });

      // Verify key column headers are present (name, label, system flag).
      // Source: list.cshtml.cs GridColumns included "action", name (200px), label.
      const headerRow = page.locator('thead tr, [role="row"]:first-child, [data-testid="entity-list-header"]');
      await expect(headerRow).toBeVisible({ timeout: DATA_TIMEOUT });

      // Name and Label should be visible columns.
      await expect(page.getByRole('columnheader', { name: /name/i })
        .or(page.locator('th').filter({ hasText: /name/i }))).toBeVisible();
      await expect(page.getByRole('columnheader', { name: /label/i })
        .or(page.locator('th').filter({ hasText: /label/i }))).toBeVisible();
    });

    /**
     * Create a new entity via the admin form.
     * Source: entity/create.cshtml.cs — BindProperty: Name, Label, LabelPlural,
     * IconName, Color, System, RecordPermissions (JSON), RecordScreenIdField.
     * EntityManager.CreateEntity() validates lowercase name, no spaces.
     */
    test('should create a new entity', async ({ page }) => {
      const entityName = uniqueName('testent');
      const entityLabel = `Test Entity ${RUN_ID}`;
      const entityLabelPlural = `Test Entities ${RUN_ID}`;

      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });

      // Click the "Create Entity" or equivalent action button.
      await page.getByRole('link', { name: /create/i })
        .or(page.getByRole('button', { name: /create/i }))
        .or(page.locator('[data-testid="create-entity-btn"]'))
        .click();

      // Wait for the create form to load.
      await page.waitForURL(/entities.*create|entities.*new/i, { timeout: DATA_TIMEOUT });

      // Fill entity creation form fields — derived from entity/create.cshtml.cs BindProperty list.
      const nameInput = page.getByLabel(/^name$/i)
        .or(page.locator('[name="name"], [data-testid="entity-name-input"]'));
      const labelInput = page.getByLabel(/^label$/i)
        .or(page.locator('[name="label"], [data-testid="entity-label-input"]'));
      const pluralInput = page.getByLabel(/plural/i)
        .or(page.locator('[name="labelPlural"], [data-testid="entity-label-plural-input"]'));

      await nameInput.fill(entityName);
      await labelInput.fill(entityLabel);
      await pluralInput.fill(entityLabelPlural);

      // Submit the form.
      await page.getByRole('button', { name: /save|create|submit/i }).click();

      // Verify success: either a notification appears or we redirect to entity details/list.
      await expect(
        page.getByText(/success|created|saved/i)
          .or(page.locator('[data-testid="success-notification"]'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Navigate back to entity list and verify the new entity is present.
      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });
      await expect(page.getByText(entityName)).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * View entity details page.
     * Source: entity/details.cshtml.cs — reads entity via EntityManager.ReadEntity(RecordId),
     * displays RecordScreenIdField, RecordPermissions grid, Clone action.
     */
    test('should view entity details', async ({ page }) => {
      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });

      // Click on the first entity row to navigate to its detail page.
      const firstEntityLink = page.locator('table tbody tr a, [data-testid="entity-row"] a').first();
      await firstEntityLink.click();

      // Verify the entity detail page loads with core information.
      await expect(
        page.getByText(/details|entity\s+details/i)
          .or(page.locator('[data-testid="entity-details"]'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Verify entity name/label are displayed.
      const entityNameDisplay = page.locator('[data-testid="entity-name"], h1, h2').first();
      await expect(entityNameDisplay).toBeVisible();

      // Verify fields and relations summary info is visible.
      // Source: details.cshtml.cs showed fields count, relations count, pages.
      await expect(
        page.getByText(/fields/i)
          .or(page.locator('[data-testid="entity-fields-count"]'))
      ).toBeVisible();
      await expect(
        page.getByText(/relations/i)
          .or(page.locator('[data-testid="entity-relations-count"]'))
      ).toBeVisible();
    });

    /**
     * Edit an entity (modify label or description).
     * Source: entity/manage.cshtml.cs — same BindProperty fields as create.
     * Fetches entity by ID, allows modification, validates, saves.
     */
    test('should edit an entity', async ({ page }) => {
      // First create a test entity to edit.
      const entityName = uniqueName('editent');
      const entityLabel = `Edit Entity ${RUN_ID}`;
      const updatedLabel = `Updated Entity ${RUN_ID}`;

      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });

      // Create an entity first.
      await page.getByRole('link', { name: /create/i })
        .or(page.getByRole('button', { name: /create/i }))
        .or(page.locator('[data-testid="create-entity-btn"]'))
        .click();

      await page.waitForURL(/entities.*create|entities.*new/i, { timeout: DATA_TIMEOUT });

      await (page.getByLabel(/^name$/i)
        .or(page.locator('[name="name"]'))).fill(entityName);
      await (page.getByLabel(/^label$/i)
        .or(page.locator('[name="label"]'))).fill(entityLabel);
      await (page.getByLabel(/plural/i)
        .or(page.locator('[name="labelPlural"]'))).fill(`${entityLabel}s`);

      await page.getByRole('button', { name: /save|create|submit/i }).click();
      await expect(
        page.getByText(/success|created|saved/i)
          .or(page.locator('[data-testid="success-notification"]'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Navigate to the entity list, find the entity, and go to manage/edit.
      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });
      await page.getByText(entityName).click();

      // Click "Edit" or "Manage" action on the entity detail page.
      await page.getByRole('link', { name: /edit|manage/i })
        .or(page.getByRole('button', { name: /edit|manage/i }))
        .or(page.locator('[data-testid="edit-entity-btn"]'))
        .click();

      // Update the label field.
      const labelInput = page.getByLabel(/^label$/i)
        .or(page.locator('[name="label"]'));
      await labelInput.clear();
      await labelInput.fill(updatedLabel);

      // Submit changes.
      await page.getByRole('button', { name: /save|update|submit/i }).click();

      // Verify the update succeeded.
      await expect(
        page.getByText(/success|updated|saved/i)
          .or(page.locator('[data-testid="success-notification"]'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Confirm the updated label is visible.
      await expect(page.getByText(updatedLabel)).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Delete a non-system entity.
     * Source: entity/details.cshtml.cs — System entities are protected from deletion.
     * Custom entities can be deleted via a delete action that calls EntityManager.DeleteEntity().
     */
    test('should delete a non-system entity', async ({ page }) => {
      // Create a disposable entity for deletion.
      const entityName = uniqueName('delent');
      const entityLabel = `Delete Entity ${RUN_ID}`;

      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });

      await page.getByRole('link', { name: /create/i })
        .or(page.getByRole('button', { name: /create/i }))
        .or(page.locator('[data-testid="create-entity-btn"]'))
        .click();

      await page.waitForURL(/entities.*create|entities.*new/i, { timeout: DATA_TIMEOUT });

      await (page.getByLabel(/^name$/i)
        .or(page.locator('[name="name"]'))).fill(entityName);
      await (page.getByLabel(/^label$/i)
        .or(page.locator('[name="label"]'))).fill(entityLabel);
      await (page.getByLabel(/plural/i)
        .or(page.locator('[name="labelPlural"]'))).fill(`${entityLabel}s`);

      await page.getByRole('button', { name: /save|create|submit/i }).click();
      await expect(
        page.getByText(/success|created|saved/i)
          .or(page.locator('[data-testid="success-notification"]'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Navigate to entity list, find the entity.
      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });
      await expect(page.getByText(entityName)).toBeVisible({ timeout: DATA_TIMEOUT });

      // Click on the entity to go to its detail page.
      await page.getByText(entityName).click();

      // Initiate deletion.
      await page.getByRole('button', { name: /delete/i })
        .or(page.locator('[data-testid="delete-entity-btn"]'))
        .click();

      // Handle confirmation dialog if present.
      const confirmBtn = page.getByRole('button', { name: /confirm|yes|delete/i })
        .or(page.locator('[data-testid="confirm-delete-btn"]'));
      if (await confirmBtn.isVisible({ timeout: 3_000 }).catch(() => false)) {
        await confirmBtn.click();
      }

      // Verify the entity is deleted — should redirect to list or show success.
      await expect(
        page.getByText(/deleted|removed|success/i)
          .or(page.locator('[data-testid="success-notification"]'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Confirm the entity no longer appears in the list.
      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });
      await expect(page.getByText(entityName)).not.toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Verify that system entities cannot be deleted.
     * Source: entity/details.cshtml.cs — System=true entities are protected.
     */
    test('should not allow deleting system entities', async ({ page }) => {
      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });

      // Look for a system entity (e.g., "user" or "role" — these are always system entities).
      const systemEntityLink = page.getByText(/^user$/i)
        .or(page.getByText(/^role$/i))
        .or(page.locator('[data-testid="entity-row"]').filter({ hasText: /system/i }).first());

      if (await systemEntityLink.isVisible({ timeout: 5_000 }).catch(() => false)) {
        await systemEntityLink.click();

        // On the detail page, verify the delete button is either absent or disabled.
        const deleteBtn = page.getByRole('button', { name: /delete/i })
          .or(page.locator('[data-testid="delete-entity-btn"]'));

        const isDeleteVisible = await deleteBtn.isVisible({ timeout: 3_000 }).catch(() => false);
        if (isDeleteVisible) {
          // If visible, it should be disabled for system entities.
          await expect(deleteBtn).toBeDisabled();
        }
        // If not visible at all, that is correct — system entities have no delete action.
      }
    });
  });

  // =======================================================================
  // FIELD MANAGEMENT TESTS
  // Replaces entity/fields, entity/create-field, entity/field-details,
  //          entity/manage-field
  // =======================================================================

  test.describe('Field Management', () => {
    /**
     * Display fields for an entity.
     * Source: entity/fields.cshtml.cs — reads ErpEntity.Fields,
     * PagerSize=1000, name filtering, sorted by name.
     */
    test('should display fields for an entity', async ({ page }) => {
      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });

      // Click on the first entity to view its details.
      const firstEntityLink = page.locator('table tbody tr a, [data-testid="entity-row"] a').first();
      await firstEntityLink.click();

      // Navigate to the fields tab or section.
      await page.getByRole('tab', { name: /fields/i })
        .or(page.getByRole('link', { name: /fields/i }))
        .or(page.locator('[data-testid="fields-tab"]'))
        .click();

      // Verify the fields list/table renders with expected columns.
      const fieldTable = page.locator('table, [role="grid"], [data-testid="field-list"]');
      await expect(fieldTable).toBeVisible({ timeout: DATA_TIMEOUT });

      // Verify key column headers: name, label, type, required, system.
      await expect(
        page.getByRole('columnheader', { name: /name/i })
          .or(page.locator('th').filter({ hasText: /name/i }))
      ).toBeVisible();
      await expect(
        page.getByRole('columnheader', { name: /type/i })
          .or(page.locator('th').filter({ hasText: /type/i }))
      ).toBeVisible();
    });

    /**
     * Add a new text field to an entity.
     * Source: entity/create-field-select.cshtml.cs (type picker) → entity/create-field.cshtml.cs
     * BindProperty: FieldTypeId (default 18=text), Name, Label, Required, Description,
     * Unique, HelpText, System, PlaceholderText, Searchable, EnableSecurity, DefaultValue.
     * 20+ field types from WebVella.Erp/Database/FieldTypes/ are selectable.
     */
    test('should add a new field to an entity', async ({ page }) => {
      const fieldName = uniqueName('testfld');
      const fieldLabel = `Test Field ${RUN_ID}`;

      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });

      // Select an entity and go to its fields tab.
      const firstEntityLink = page.locator('table tbody tr a, [data-testid="entity-row"] a').first();
      await firstEntityLink.click();

      await page.getByRole('tab', { name: /fields/i })
        .or(page.getByRole('link', { name: /fields/i }))
        .or(page.locator('[data-testid="fields-tab"]'))
        .click();

      // Click "Create Field" or "Add Field" button.
      await page.getByRole('link', { name: /create|add/i })
        .or(page.getByRole('button', { name: /create|add/i }))
        .or(page.locator('[data-testid="create-field-btn"]'))
        .click();

      // Select field type — the monolith had a type selector page first.
      // In the React SPA, this may be a dropdown or type selection card.
      const typeSelector = page.getByLabel(/field\s*type/i)
        .or(page.locator('[data-testid="field-type-select"]'))
        .or(page.locator('select[name="fieldType"]'));
      if (await typeSelector.isVisible({ timeout: 3_000 }).catch(() => false)) {
        // selectOption requires a string label — pick the first option matching "text".
        const textOption = typeSelector.locator('option').filter({ hasText: /text/i }).first();
        const textOptionLabel = await textOption.textContent().catch(() => null);
        if (textOptionLabel) {
          await typeSelector.selectOption({ label: textOptionLabel.trim() });
        } else {
          // Fallback: select by index (first non-placeholder option).
          await typeSelector.selectOption({ index: 1 });
        }
      } else {
        // If it's a card-based type picker, click the text type card.
        const textTypeCard = page.getByRole('button', { name: /text/i })
          .or(page.locator('[data-testid="field-type-text"]'));
        if (await textTypeCard.isVisible({ timeout: 3_000 }).catch(() => false)) {
          await textTypeCard.click();
        }
      }

      // Fill field creation form.
      const nameInput = page.getByLabel(/^name$/i)
        .or(page.locator('[name="name"], [data-testid="field-name-input"]'));
      const labelInput = page.getByLabel(/^label$/i)
        .or(page.locator('[name="label"], [data-testid="field-label-input"]'));

      await nameInput.fill(fieldName);
      await labelInput.fill(fieldLabel);

      // Optionally toggle required and searchable flags.
      const requiredCheckbox = page.getByLabel(/required/i)
        .or(page.locator('[name="required"], [data-testid="field-required-checkbox"]'));
      if (await requiredCheckbox.isVisible({ timeout: 2_000 }).catch(() => false)) {
        await requiredCheckbox.check();
      }

      const searchableCheckbox = page.getByLabel(/searchable/i)
        .or(page.locator('[name="searchable"], [data-testid="field-searchable-checkbox"]'));
      if (await searchableCheckbox.isVisible({ timeout: 2_000 }).catch(() => false)) {
        await searchableCheckbox.check();
      }

      // Submit the form.
      await page.getByRole('button', { name: /save|create|submit/i }).click();

      // Verify success.
      await expect(
        page.getByText(/success|created|saved/i)
          .or(page.locator('[data-testid="success-notification"]'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Verify the new field is visible in the field list.
      await expect(page.getByText(fieldName)).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * View field details.
     * Source: entity/field-details.cshtml.cs — shows field configuration
     * (type, name, label, required, unique, help text, etc.).
     */
    test('should view field details', async ({ page }) => {
      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });

      // Navigate to an entity's fields.
      const firstEntityLink = page.locator('table tbody tr a, [data-testid="entity-row"] a').first();
      await firstEntityLink.click();

      await page.getByRole('tab', { name: /fields/i })
        .or(page.getByRole('link', { name: /fields/i }))
        .or(page.locator('[data-testid="fields-tab"]'))
        .click();

      // Click on the first field in the list.
      const firstFieldLink = page.locator('table tbody tr a, [data-testid="field-row"] a').first();
      await firstFieldLink.click();

      // Verify field detail page loads with configuration info.
      await expect(
        page.getByText(/field\s*details|field\s*configuration/i)
          .or(page.locator('[data-testid="field-details"]'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Key details should be visible: name, type, label.
      await expect(
        page.getByText(/name/i).first()
      ).toBeVisible();
      await expect(
        page.getByText(/type/i).first()
      ).toBeVisible();
    });

    /**
     * Edit a field (modify label or required flag).
     * Source: entity/manage-field.cshtml.cs — same BindProperty fields as create.
     */
    test('should edit a field', async ({ page }) => {
      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });

      // Navigate to an entity's fields.
      const firstEntityLink = page.locator('table tbody tr a, [data-testid="entity-row"] a').first();
      await firstEntityLink.click();

      await page.getByRole('tab', { name: /fields/i })
        .or(page.getByRole('link', { name: /fields/i }))
        .or(page.locator('[data-testid="fields-tab"]'))
        .click();

      // Click on the first field to view details.
      const firstFieldLink = page.locator('table tbody tr a, [data-testid="field-row"] a').first();
      await firstFieldLink.click();

      // Click "Edit" or "Manage" on the field detail page.
      await page.getByRole('link', { name: /edit|manage/i })
        .or(page.getByRole('button', { name: /edit|manage/i }))
        .or(page.locator('[data-testid="edit-field-btn"]'))
        .click();

      // Modify the label.
      const labelInput = page.getByLabel(/^label$/i)
        .or(page.locator('[name="label"]'));
      const originalLabel = await labelInput.inputValue();
      const newLabel = `${originalLabel} Updated`;
      await labelInput.clear();
      await labelInput.fill(newLabel);

      // Submit.
      await page.getByRole('button', { name: /save|update|submit/i }).click();

      // Verify the update succeeded.
      await expect(
        page.getByText(/success|updated|saved/i)
          .or(page.locator('[data-testid="success-notification"]'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });
    });
  });

  // =======================================================================
  // RELATION MANAGEMENT TESTS
  // Replaces entity/relations, entity/relation-create
  // =======================================================================

  test.describe('Relation Management', () => {
    /**
     * Display entity relations.
     * Source: entity/relations.cshtml.cs — filters relations where
     * TargetEntityId == ErpEntity.Id || OriginEntityId == ErpEntity.Id,
     * name filtering, PagerSize=1000.
     */
    test('should display entity relations', async ({ page }) => {
      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });

      // Navigate to an entity.
      const firstEntityLink = page.locator('table tbody tr a, [data-testid="entity-row"] a').first();
      await firstEntityLink.click();

      // Click on the Relations tab.
      await page.getByRole('tab', { name: /relations/i })
        .or(page.getByRole('link', { name: /relations/i }))
        .or(page.locator('[data-testid="relations-tab"]'))
        .click();

      // Verify the relations section/table is visible.
      const relationsContainer = page.locator(
        'table, [role="grid"], [data-testid="relation-list"], [data-testid="relations-section"]'
      );
      await expect(relationsContainer).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Create a new relation between two entities.
     * Source: entity/relation-create.cshtml.cs — requires name, label,
     * relation type (1:N or N:N), origin entity, target entity.
     * EntityRelationManager.Create() validates the relation.
     */
    test('should create a new relation', async ({ page }) => {
      const relationName = uniqueName('testrel');
      const relationLabel = `Test Relation ${RUN_ID}`;

      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });

      // Navigate to an entity's relations tab.
      const firstEntityLink = page.locator('table tbody tr a, [data-testid="entity-row"] a').first();
      await firstEntityLink.click();

      await page.getByRole('tab', { name: /relations/i })
        .or(page.getByRole('link', { name: /relations/i }))
        .or(page.locator('[data-testid="relations-tab"]'))
        .click();

      // Click "Create Relation" button.
      await page.getByRole('link', { name: /create/i })
        .or(page.getByRole('button', { name: /create/i }))
        .or(page.locator('[data-testid="create-relation-btn"]'))
        .click();

      // Fill the relation creation form.
      const nameInput = page.getByLabel(/^name$/i)
        .or(page.locator('[name="name"], [data-testid="relation-name-input"]'));
      const labelInput = page.getByLabel(/^label$/i)
        .or(page.locator('[name="label"], [data-testid="relation-label-input"]'));

      await nameInput.fill(relationName);
      await labelInput.fill(relationLabel);

      // Select relation type (1:N or N:N).
      const typeSelector = page.getByLabel(/type/i)
        .or(page.locator('[name="relationType"], [data-testid="relation-type-select"]'));
      if (await typeSelector.isVisible({ timeout: 3_000 }).catch(() => false)) {
        // Pick 1:N (one-to-many) as the default test option.
        await typeSelector.selectOption({ index: 0 });
      }

      // Select origin and target entities if the selects are present.
      const originSelector = page.getByLabel(/origin/i)
        .or(page.locator('[name="originEntity"], [data-testid="relation-origin-select"]'));
      if (await originSelector.isVisible({ timeout: 2_000 }).catch(() => false)) {
        await originSelector.selectOption({ index: 0 });
      }

      const targetSelector = page.getByLabel(/target/i)
        .or(page.locator('[name="targetEntity"], [data-testid="relation-target-select"]'));
      if (await targetSelector.isVisible({ timeout: 2_000 }).catch(() => false)) {
        await targetSelector.selectOption({ index: 1 });
      }

      // Submit.
      await page.getByRole('button', { name: /save|create|submit/i }).click();

      // Verify success.
      await expect(
        page.getByText(/success|created|saved/i)
          .or(page.locator('[data-testid="success-notification"]'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });
    });
  });

  // =======================================================================
  // ROLE MANAGEMENT TESTS
  // Replaces role/list, role/create, role/manage
  // =======================================================================

  test.describe('Role Management', () => {
    /**
     * Display role list.
     * Source: role/list.cshtml.cs — EQL: SELECT * FROM role.
     * Grid columns: action, name (200px), description.
     */
    test('should display role list', async ({ page }) => {
      await page.goto(ROLES_URL, { waitUntil: 'networkidle' });

      // Verify the roles table renders.
      const table = page.locator('table, [role="grid"], [data-testid="role-list"]');
      await expect(table).toBeVisible({ timeout: DATA_TIMEOUT });

      // Verify column headers: name, description.
      await expect(
        page.getByRole('columnheader', { name: /name/i })
          .or(page.locator('th').filter({ hasText: /name/i }))
      ).toBeVisible();
      await expect(
        page.getByRole('columnheader', { name: /description/i })
          .or(page.locator('th').filter({ hasText: /description/i }))
      ).toBeVisible();

      // Verify system roles are present: Administrator, Regular, Guest.
      // Source: Definitions.cs SystemIds defines these three roles.
      await expect(page.getByText(/administrator/i)).toBeVisible({ timeout: DATA_TIMEOUT });
      await expect(page.getByText(/regular/i)).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Create a new role.
     * Source: role/create.cshtml.cs — BindProperty: Name, Description.
     * SecurityManager().SaveRole(newRole) validates and persists.
     */
    test('should create a new role', async ({ page }) => {
      const roleName = uniqueName('testrole');
      const roleDescription = `Test role created during E2E run ${RUN_ID}`;

      await page.goto(ROLES_URL, { waitUntil: 'networkidle' });

      // Click "Create Role" button.
      await page.getByRole('link', { name: /create/i })
        .or(page.getByRole('button', { name: /create/i }))
        .or(page.locator('[data-testid="create-role-btn"]'))
        .click();

      // Fill the role creation form.
      const nameInput = page.getByLabel(/^name$/i)
        .or(page.locator('[name="name"], [data-testid="role-name-input"]'));
      const descInput = page.getByLabel(/description/i)
        .or(page.locator('[name="description"], [data-testid="role-description-input"]'));

      await nameInput.fill(roleName);
      await descInput.fill(roleDescription);

      // Submit.
      await page.getByRole('button', { name: /save|create|submit/i }).click();

      // Verify success.
      await expect(
        page.getByText(/success|created|saved/i)
          .or(page.locator('[data-testid="success-notification"]'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Navigate back and verify role is in the list.
      await page.goto(ROLES_URL, { waitUntil: 'networkidle' });
      await expect(page.getByText(roleName)).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Edit a role (modify description).
     * Source: role/manage.cshtml.cs — fetches via EQL, edits Name/Description,
     * SecurityManager().SaveRole(role). Catches ValidationException.
     */
    test('should edit a role', async ({ page }) => {
      // Create a role first to have a clean target for editing.
      const roleName = uniqueName('editrole');
      const roleDescription = `Edit role ${RUN_ID}`;
      const updatedDescription = `Updated role description ${RUN_ID}`;

      await page.goto(ROLES_URL, { waitUntil: 'networkidle' });

      // Create role.
      await page.getByRole('link', { name: /create/i })
        .or(page.getByRole('button', { name: /create/i }))
        .or(page.locator('[data-testid="create-role-btn"]'))
        .click();

      await (page.getByLabel(/^name$/i)
        .or(page.locator('[name="name"]'))).fill(roleName);
      await (page.getByLabel(/description/i)
        .or(page.locator('[name="description"]'))).fill(roleDescription);

      await page.getByRole('button', { name: /save|create|submit/i }).click();
      await expect(
        page.getByText(/success|created|saved/i)
          .or(page.locator('[data-testid="success-notification"]'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Navigate to role list and find the created role.
      await page.goto(ROLES_URL, { waitUntil: 'networkidle' });
      await page.getByText(roleName).click();

      // Click edit/manage.
      await page.getByRole('link', { name: /edit|manage/i })
        .or(page.getByRole('button', { name: /edit|manage/i }))
        .or(page.locator('[data-testid="edit-role-btn"]'))
        .click();

      // Update description.
      const descInput = page.getByLabel(/description/i)
        .or(page.locator('[name="description"]'));
      await descInput.clear();
      await descInput.fill(updatedDescription);

      // Submit.
      await page.getByRole('button', { name: /save|update|submit/i }).click();

      // Verify success.
      await expect(
        page.getByText(/success|updated|saved/i)
          .or(page.locator('[data-testid="success-notification"]'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Confirm the updated description is visible.
      await expect(page.getByText(updatedDescription)).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Verify validation errors on role creation with empty name.
     * Source: role/create.cshtml.cs catches ValidationException from SecurityManager.
     */
    test('should show validation error when creating role with empty name', async ({ page }) => {
      await page.goto(ROLES_URL, { waitUntil: 'networkidle' });

      await page.getByRole('link', { name: /create/i })
        .or(page.getByRole('button', { name: /create/i }))
        .or(page.locator('[data-testid="create-role-btn"]'))
        .click();

      // Leave name empty and submit.
      await page.getByRole('button', { name: /save|create|submit/i }).click();

      // Verify validation error is shown.
      await expect(
        page.getByText(/required|cannot be empty|name is required|validation/i)
          .or(page.locator('[data-testid="validation-error"]'))
          .or(page.locator('.error, .text-red, .text-danger'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });
    });
  });

  // =======================================================================
  // USER MANAGEMENT TESTS
  // Replaces user/list, user/create, user/manage
  // =======================================================================

  test.describe('User Management', () => {
    /**
     * Display user list.
     * Source: user/list.cshtml.cs — EQL: SELECT id,email,username,$user_role.name FROM user.
     * Grid columns: action, email (sortable, 120px), username (sortable), role (not sortable).
     * PagerSize=10.
     */
    test('should display user list', async ({ page }) => {
      await page.goto(USERS_URL, { waitUntil: 'networkidle' });

      // Verify the users table renders.
      const table = page.locator('table, [role="grid"], [data-testid="user-list"]');
      await expect(table).toBeVisible({ timeout: DATA_TIMEOUT });

      // Verify column headers: email, name/username, role(s), enabled.
      await expect(
        page.getByRole('columnheader', { name: /email/i })
          .or(page.locator('th').filter({ hasText: /email/i }))
      ).toBeVisible();

      // The default system user should be in the list.
      await expect(page.getByText(/erp@webvella\.com/i)).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Create a new user.
     * Source: user/create.cshtml.cs — BindProperty: UserName, Email, Password,
     * Image, FirstName, LastName, Enabled (default true), Verified (default true),
     * Roles (default includes regular role GUID).
     * In new architecture, this creates a Cognito user via Identity service.
     */
    test('should create a new user', async ({ page }) => {
      const userEmail = `${uniqueName('testuser')}@webvella-test.com`;
      const userPassword = 'TestPass123!';
      const firstName = 'Test';
      const lastName = `User${RUN_ID}`;

      await page.goto(USERS_URL, { waitUntil: 'networkidle' });

      // Click "Create User" button.
      await page.getByRole('link', { name: /create/i })
        .or(page.getByRole('button', { name: /create/i }))
        .or(page.locator('[data-testid="create-user-btn"]'))
        .click();

      // Fill user creation form — maps to user/create.cshtml.cs BindProperty fields.
      const emailInput = page.getByLabel(/email/i)
        .or(page.locator('[name="email"], [data-testid="user-email-input"]'));
      const passwordInput = page.getByLabel(/password/i)
        .or(page.locator('[name="password"], [data-testid="user-password-input"]'));
      const firstNameInput = page.getByLabel(/first\s*name/i)
        .or(page.locator('[name="firstName"], [data-testid="user-firstname-input"]'));
      const lastNameInput = page.getByLabel(/last\s*name/i)
        .or(page.locator('[name="lastName"], [data-testid="user-lastname-input"]'));

      await emailInput.fill(userEmail);
      await passwordInput.fill(userPassword);
      await firstNameInput.fill(firstName);
      await lastNameInput.fill(lastName);

      // Optionally assign a role — the monolith default includes the "regular" role.
      // RoleOptions in user/create.cshtml.cs excluded "guest".
      const roleSelector = page.getByLabel(/role/i)
        .or(page.locator('[name="roles"], [data-testid="user-role-select"]'));
      if (await roleSelector.isVisible({ timeout: 2_000 }).catch(() => false)) {
        // Select the "regular" role if available, otherwise first non-placeholder option.
        const regularOption = roleSelector.locator('option').filter({ hasText: /regular/i }).first();
        const regularLabel = await regularOption.textContent().catch(() => null);
        if (regularLabel) {
          await roleSelector.selectOption({ label: regularLabel.trim() });
        } else {
          await roleSelector.selectOption({ index: 1 });
        }
      }

      // Submit.
      await page.getByRole('button', { name: /save|create|submit/i }).click();

      // Verify success — Cognito user created via Identity service.
      await expect(
        page.getByText(/success|created|saved/i)
          .or(page.locator('[data-testid="success-notification"]'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Verify new user appears in the list.
      await page.goto(USERS_URL, { waitUntil: 'networkidle' });
      await expect(page.getByText(userEmail)).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Edit a user (modify roles or name).
     * Source: user/manage.cshtml.cs — fetches via EQL with $user_role.id,
     * same BindProperty fields as create. RoleOptions exclude "guest".
     */
    test('should edit a user', async ({ page }) => {
      // Create a user first to edit.
      const userEmail = `${uniqueName('edituser')}@webvella-test.com`;
      const userPassword = 'TestPass123!';
      const firstName = 'Edit';
      const lastName = `User${RUN_ID}`;
      const updatedFirstName = 'Updated';

      await page.goto(USERS_URL, { waitUntil: 'networkidle' });

      // Create user.
      await page.getByRole('link', { name: /create/i })
        .or(page.getByRole('button', { name: /create/i }))
        .or(page.locator('[data-testid="create-user-btn"]'))
        .click();

      await (page.getByLabel(/email/i)
        .or(page.locator('[name="email"]'))).fill(userEmail);
      await (page.getByLabel(/password/i)
        .or(page.locator('[name="password"]'))).fill(userPassword);
      await (page.getByLabel(/first\s*name/i)
        .or(page.locator('[name="firstName"]'))).fill(firstName);
      await (page.getByLabel(/last\s*name/i)
        .or(page.locator('[name="lastName"]'))).fill(lastName);

      await page.getByRole('button', { name: /save|create|submit/i }).click();
      await expect(
        page.getByText(/success|created|saved/i)
          .or(page.locator('[data-testid="success-notification"]'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      // Navigate to user list and find the created user.
      await page.goto(USERS_URL, { waitUntil: 'networkidle' });
      await page.getByText(userEmail).click();

      // Click edit/manage.
      await page.getByRole('link', { name: /edit|manage/i })
        .or(page.getByRole('button', { name: /edit|manage/i }))
        .or(page.locator('[data-testid="edit-user-btn"]'))
        .click();

      // Update first name.
      const firstNameInput = page.getByLabel(/first\s*name/i)
        .or(page.locator('[name="firstName"]'));
      await firstNameInput.clear();
      await firstNameInput.fill(updatedFirstName);

      // Submit.
      await page.getByRole('button', { name: /save|update|submit/i }).click();

      // Verify success.
      await expect(
        page.getByText(/success|updated|saved/i)
          .or(page.locator('[data-testid="success-notification"]'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Verify validation errors on user creation with missing required fields.
     * Source: user/create.cshtml.cs — Cognito requires valid email + password.
     */
    test('should show validation error when creating user with empty email', async ({ page }) => {
      await page.goto(USERS_URL, { waitUntil: 'networkidle' });

      await page.getByRole('link', { name: /create/i })
        .or(page.getByRole('button', { name: /create/i }))
        .or(page.locator('[data-testid="create-user-btn"]'))
        .click();

      // Fill only password, leave email empty.
      await (page.getByLabel(/password/i)
        .or(page.locator('[name="password"]'))).fill('TestPass123!');

      // Submit.
      await page.getByRole('button', { name: /save|create|submit/i }).click();

      // Verify validation error.
      await expect(
        page.getByText(/required|email.*required|valid.*email|validation/i)
          .or(page.locator('[data-testid="validation-error"]'))
          .or(page.locator('.error, .text-red, .text-danger'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Verify that the default system user (erp@webvella.com) has admin role.
     * Source: Definitions.cs — SystemIds.FirstUserId is the system admin.
     * AAP §0.7.5 — default system user is seeded with admin role.
     */
    test('should display system admin user with administrator role', async ({ page }) => {
      await page.goto(USERS_URL, { waitUntil: 'networkidle' });

      // Find the system admin user row.
      const adminRow = page.locator('tr, [data-testid="user-row"]')
        .filter({ hasText: /erp@webvella\.com/i });

      await expect(adminRow).toBeVisible({ timeout: DATA_TIMEOUT });

      // Verify the admin role is associated.
      await expect(
        adminRow.getByText(/admin/i)
          .or(adminRow.locator('[data-testid="user-role"]').filter({ hasText: /admin/i }))
      ).toBeVisible();
    });
  });

  // =======================================================================
  // ADMIN NAVIGATION TESTS
  // Validates the admin console navigation structure.
  // =======================================================================

  test.describe('Admin Navigation', () => {
    /**
     * Verify the admin console has navigation links to all major sections.
     * Source: SDK plugin organized pages into entity/, role/, user/,
     * application/, data_source/, job/, tools/ subfolders.
     */
    test('should display admin navigation with section links', async ({ page }) => {
      await page.goto(ADMIN_URL, { waitUntil: 'networkidle' });

      // Verify navigation links to major admin sections are present.
      await expect(
        page.getByRole('link', { name: /entities/i })
          .or(page.locator('[data-testid="admin-nav-entities"]'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      await expect(
        page.getByRole('link', { name: /roles/i })
          .or(page.locator('[data-testid="admin-nav-roles"]'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });

      await expect(
        page.getByRole('link', { name: /users/i })
          .or(page.locator('[data-testid="admin-nav-users"]'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Verify admin console requires admin privileges.
     * Non-admin users should be denied access (redirected or shown error).
     */
    test('should require admin role for admin console access', async ({ page, context }) => {
      // Clear current admin session.
      await context.clearCookies();
      for (const p of context.pages()) {
        try {
          await p.evaluate(() => {
            try { localStorage.clear(); } catch { /* safe */ }
            try { sessionStorage.clear(); } catch { /* safe */ }
          });
        } catch {
          // Page may not yet have a valid origin — ignore safely
        }
      }

      // Attempt to navigate to admin without authentication.
      await page.goto(ADMIN_URL, { waitUntil: 'networkidle' });

      // Should be redirected to login or shown an access denied message.
      const isOnLogin = page.url().includes('/login');
      const hasForbidden = await page.getByText(/forbidden|access denied|unauthorized|not authorized/i)
        .isVisible({ timeout: 5_000 }).catch(() => false);

      expect(isOnLogin || hasForbidden).toBeTruthy();
    });
  });

  // =======================================================================
  // ENTITY CREATION VALIDATION TESTS
  // Validates form-level error handling for entity creation.
  // =======================================================================

  test.describe('Entity Creation Validation', () => {
    /**
     * Verify validation error when entity name is empty.
     * Source: entity/create.cshtml.cs — EntityManager.CreateEntity() validates
     * that Name is non-empty, lowercase, and contains no spaces.
     */
    test('should show validation error for empty entity name', async ({ page }) => {
      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });

      await page.getByRole('link', { name: /create/i })
        .or(page.getByRole('button', { name: /create/i }))
        .or(page.locator('[data-testid="create-entity-btn"]'))
        .click();

      await page.waitForURL(/entities.*create|entities.*new/i, { timeout: DATA_TIMEOUT });

      // Fill only label, leave name empty.
      await (page.getByLabel(/^label$/i)
        .or(page.locator('[name="label"]'))).fill('Test Label');

      // Submit.
      await page.getByRole('button', { name: /save|create|submit/i }).click();

      // Verify validation error.
      await expect(
        page.getByText(/required|name.*required|name cannot be empty|validation/i)
          .or(page.locator('[data-testid="validation-error"]'))
          .or(page.locator('.error, .text-red, .text-danger'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    /**
     * Verify that entity names must be lowercase with no spaces.
     * Source: EntityManager validates name format constraints.
     */
    test('should enforce lowercase name constraint for entities', async ({ page }) => {
      await page.goto(ENTITIES_URL, { waitUntil: 'networkidle' });

      await page.getByRole('link', { name: /create/i })
        .or(page.getByRole('button', { name: /create/i }))
        .or(page.locator('[data-testid="create-entity-btn"]'))
        .click();

      await page.waitForURL(/entities.*create|entities.*new/i, { timeout: DATA_TIMEOUT });

      // Try to create entity with uppercase/spaces in name.
      await (page.getByLabel(/^name$/i)
        .or(page.locator('[name="name"]'))).fill('Invalid Name With Spaces');
      await (page.getByLabel(/^label$/i)
        .or(page.locator('[name="label"]'))).fill('Test');
      await (page.getByLabel(/plural/i)
        .or(page.locator('[name="labelPlural"]'))).fill('Tests');

      await page.getByRole('button', { name: /save|create|submit/i }).click();

      // Verify validation error about name format.
      await expect(
        page.getByText(/lowercase|invalid.*name|no spaces|format|alphanumeric|validation/i)
          .or(page.locator('[data-testid="validation-error"]'))
          .or(page.locator('.error, .text-red, .text-danger'))
      ).toBeVisible({ timeout: DATA_TIMEOUT });
    });
  });
});
