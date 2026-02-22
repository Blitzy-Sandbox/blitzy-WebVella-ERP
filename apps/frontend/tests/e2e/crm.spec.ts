/**
 * @file CRM E2E Test Suite — WebVella ERP React SPA
 *
 * Comprehensive Playwright E2E test suite validating all critical CRM
 * (Customer Relationship Management) user-facing workflows against a full
 * LocalStack stack (API Gateway → Lambda handlers → DynamoDB → CRM service).
 *
 * Replaces the monolith's CRM/Next plugin Razor-Page-driven user flows:
 *
 *   WebVella.Erp.Plugins.Next/NextPlugin.20190204.cs
 *     Creates account entity (2e22b50f-e444-4b62-a171-076e51246939) with fields:
 *       name (text, required), type (select: Company="1"/Person="2"),
 *       website (url), email (email), fixed_phone, mobile_phone, fax_phone,
 *       street, street_2, city, region, post_code, country_id, tax_id, notes,
 *       x_search (auto-generated).
 *     Creates contact entity (39e1dd9b-827f-464d-95ea-507ade81cbd0) with fields:
 *       first_name (text, required), last_name (text, required), email,
 *       job_title, fixed_phone, mobile_phone, fax_phone, city, region,
 *       street, street_2, post_code, country_id, notes.
 *     Creates account_nn_contact ManyToMany relation (dd211c99-5415-4195-923a-cb5a56e5d544).
 *
 *   WebVella.Erp.Plugins.Next/NextPlugin.20190206.cs
 *     Creates salutation entity with seeded records: Mr., Ms., Mrs., Dr., Prof.
 *     Adds salutation_id (required, default Mr.) to both account and contact.
 *     Adds photo (image) and created_on (datetime) to contact.
 *     Creates salutation_1n_account and salutation_1n_contact relations.
 *
 *   WebVella.Erp.Plugins.Next/Services/SearchService.cs
 *     Maintains x_search field via RegenSearchField() on post-create/update hooks.
 *
 *   WebVella.Erp.Plugins.Next/Configuration.cs
 *     AccountSearchIndexFields: city, country, email, fax_phone, first_name,
 *       fixed_phone, last_name, mobile_phone, name, notes, post_code, region,
 *       street, street_2, tax_id, type, website.
 *     ContactSearchIndexFields: city, country, account name, email, fax_phone,
 *       first_name, fixed_phone, job_title, last_name, mobile_phone, notes,
 *       post_code, region, street, street_2.
 *
 *   WebVella.Erp.Plugins.Next/Hooks/Api/AccountHook.cs
 *   WebVella.Erp.Plugins.Next/Hooks/Api/ContactHook.cs
 *     Post-create/update hooks that trigger SearchService.RegenSearchField().
 *
 * The React SPA replaces all CRM Razor Pages with route-based views:
 *
 *   GET    /crm/accounts                    → AccountList
 *   GET    /crm/accounts/create             → AccountCreate
 *   GET    /crm/accounts/:id                → AccountDetails
 *   GET    /crm/accounts/:id/edit           → AccountManage/Edit
 *   GET    /crm/contacts                    → ContactList
 *   GET    /crm/contacts/create             → ContactCreate
 *   GET    /crm/contacts/:id                → ContactDetails
 *   GET    /crm/contacts/:id/edit           → ContactManage/Edit
 *
 * API endpoints (CRM microservice):
 *   GET    /v1/accounts          → list accounts
 *   POST   /v1/accounts          → create account
 *   GET    /v1/accounts/:id      → get account
 *   PUT    /v1/accounts/:id      → update account
 *   DELETE /v1/accounts/:id      → delete account
 *   GET    /v1/contacts          → list contacts
 *   POST   /v1/contacts          → create contact
 *   GET    /v1/contacts/:id      → get contact
 *   PUT    /v1/contacts/:id      → update contact
 *   DELETE /v1/contacts/:id      → delete contact
 *   POST   /v1/accounts/:id/contacts/:contactId   → link contact
 *   DELETE /v1/accounts/:id/contacts/:contactId   → unlink contact
 *
 * Domain events (SNS → SQS):
 *   crm.account.created, crm.account.updated, crm.account.deleted
 *   crm.contact.created, crm.contact.updated, crm.contact.deleted
 *   crm.relation.created, crm.relation.deleted
 *
 * Testing pattern (AAP §0.8.1 & §0.8.4):
 *   1. docker compose up -d       — start LocalStack + Step Functions Local
 *   2. npx nx e2e frontend-e2e    — run all E2E tests against LocalStack
 *   3. docker compose down        — tear down LocalStack
 *
 * ALL tests execute against a real LocalStack instance — zero mocked AWS
 * SDK calls (AAP §0.8.4). The CRM service is self-contained with its own
 * DynamoDB datastore (AAP §0.8.1: self-contained bounded contexts).
 *
 * Performance targets (AAP §0.8.2):
 *   Lambda cold start (.NET Native AOT) < 1 second
 *   API response P95 (warm) < 500ms
 *   DynamoDB read latency P99 < 10ms
 *
 * @see WebVella.Erp.Plugins.Next/NextPlugin.20190204.cs   — Account/Contact entity creation
 * @see WebVella.Erp.Plugins.Next/NextPlugin.20190206.cs   — Salutation entity + seeded values
 * @see WebVella.Erp.Plugins.Next/Services/SearchService.cs — x_search field regeneration
 * @see WebVella.Erp.Plugins.Next/Configuration.cs          — Search index field definitions
 * @see WebVella.Erp.Plugins.Next/Hooks/Api/AccountHook.cs  — Account post-CRUD hooks
 * @see WebVella.Erp.Plugins.Next/Hooks/Api/ContactHook.cs  — Contact post-CRUD hooks
 * @see WebVella.Erp.Plugins.Crm/CrmPlugin.cs               — CRM plugin entry point
 */

import { test, expect, Page, BrowserContext } from '@playwright/test';
import { login } from './auth.spec';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Base URL for the React SPA frontend (Vite dev server or production build). */
const BASE_URL: string =
  process.env.PLAYWRIGHT_BASE_URL || 'http://localhost:5173';

/** CRM section root route — replaces monolith's Next/CRM plugin routes. */
const CRM_URL = '/crm';

/** Account list page route. */
const ACCOUNTS_URL = `${CRM_URL}/accounts`;

/** Contact list page route. */
const CONTACTS_URL = `${CRM_URL}/contacts`;

/**
 * Maximum time (ms) to wait for API-backed data rendering.
 * Accounts for Lambda cold start latency (< 1s .NET AOT, < 3s Node.js)
 * and DynamoDB response times (P99 < 10ms).
 */
const DATA_TIMEOUT = 15_000;

/**
 * Maximum time (ms) to wait for Cognito-backed authentication to complete.
 * Matches auth.spec.ts AUTH_TIMEOUT for consistency.
 */
const AUTH_TIMEOUT = 15_000;

/** Short settle time (ms) after navigation for DOM and state updates. */
const SETTLE_TIME = 500;

/**
 * Unique suffix for all test resources created during this run.
 * Prevents collisions across parallel test executions and makes cleanup
 * deterministic. Uses base-36 timestamp encoding for compact strings.
 */
const RUN_ID = `e2e${Date.now().toString(36)}`;

// ---------------------------------------------------------------------------
// Reusable Helpers
// ---------------------------------------------------------------------------

/**
 * Generates a unique human-readable name for test data creation.
 * The CRM service requires non-empty name fields; this guarantees uniqueness
 * across parallel test runs and avoids collisions with seeded data.
 *
 * @param prefix - Short human-readable prefix (e.g., "Acme", "JohnDoe").
 * @returns A unique string incorporating the run ID and timestamp.
 */
function uniqueName(prefix: string): string {
  return `${prefix} ${RUN_ID}`;
}

/**
 * Waits for a navigation or API response to complete with tolerance for
 * Lambda cold starts. Uses a combination of URL monitoring and network
 * idle detection to handle the variable latency of LocalStack-backed APIs.
 *
 * @param page - Playwright Page instance.
 * @param urlPattern - String or RegExp the target URL must match.
 * @param timeout - Maximum wait time in milliseconds (default: DATA_TIMEOUT).
 */
async function waitForNavigation(
  page: Page,
  urlPattern: string | RegExp,
  timeout: number = DATA_TIMEOUT,
): Promise<void> {
  await page.waitForURL(urlPattern, { timeout, waitUntil: 'networkidle' });
  await page.waitForTimeout(SETTLE_TIME);
}

/**
 * Safely attempts to delete a CRM resource (account or contact) by navigating
 * to its details page and activating the delete action. Returns true if the
 * deletion appeared to succeed, false otherwise. Used in afterAll cleanup.
 *
 * @param page - Playwright Page instance.
 * @param listUrl - URL of the resource list page.
 * @param resourceName - Name/text of the resource to locate.
 */
async function cleanupResource(
  page: Page,
  listUrl: string,
  resourceName: string,
): Promise<boolean> {
  try {
    await page.goto(listUrl, { waitUntil: 'networkidle', timeout: DATA_TIMEOUT });
    await page.waitForTimeout(SETTLE_TIME);

    // Locate the resource in the list by its name text
    const resourceLocator = page.getByText(resourceName, { exact: false });
    const isVisible = await resourceLocator.isVisible().catch(() => false);
    if (!isVisible) return true; // Already removed or never created

    // Click resource row to navigate to its details page
    await resourceLocator.first().click();
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(SETTLE_TIME);

    // Locate and click the delete button on the details page
    const deleteBtn = page
      .getByRole('button', { name: /delete/i })
      .or(page.locator('[data-testid="delete-btn"]'))
      .or(page.locator('button[aria-label*="delete" i]'));

    const deleteVisible = await deleteBtn.first().isVisible().catch(() => false);
    if (deleteVisible) {
      await deleteBtn.first().click();
      await page.waitForTimeout(SETTLE_TIME);

      // Accept the confirmation dialog / modal
      const confirmBtn = page
        .getByRole('button', { name: /confirm|yes|ok|delete/i })
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

/**
 * Fills a form field by its label text. Handles both regular input fields
 * and select dropdowns. Falls back to data-testid locator if label is not found.
 *
 * @param page - Playwright Page instance.
 * @param label - The visible label text or regex pattern.
 * @param value - The value to fill or select.
 */
async function fillField(
  page: Page,
  label: string | RegExp,
  value: string,
): Promise<void> {
  const labelLocator = page.getByLabel(label);
  const isVisible = await labelLocator.isVisible().catch(() => false);

  if (isVisible) {
    const tagName = await labelLocator.evaluate((el) => el.tagName.toLowerCase());
    if (tagName === 'select') {
      await labelLocator.selectOption(value);
    } else {
      await labelLocator.fill(value);
    }
  } else {
    // Fallback: try role-based text input or data-testid
    const nameStr = typeof label === 'string' ? label : label.source;
    const fallback = page
      .getByRole('textbox', { name: label })
      .or(page.locator(`[data-testid="field-${nameStr.toLowerCase().replace(/\s+/g, '-')}"]`));
    const fallbackVisible = await fallback.first().isVisible().catch(() => false);
    if (fallbackVisible) {
      await fallback.first().fill(value);
    }
  }
}

/**
 * Selects an option from a dropdown/select field by its label and option text.
 * Supports both native <select> elements and custom dropdown components.
 *
 * @param page - Playwright Page instance.
 * @param label - The label text of the select field.
 * @param optionText - The visible text of the option to select.
 */
async function selectDropdown(
  page: Page,
  label: string | RegExp,
  optionText: string,
): Promise<void> {
  const selectLocator = page.getByLabel(label);
  const isVisible = await selectLocator.isVisible().catch(() => false);

  if (isVisible) {
    const tagName = await selectLocator.evaluate((el) => el.tagName.toLowerCase());
    if (tagName === 'select') {
      // Native select: try by label text first, then by value
      await selectLocator.selectOption({ label: optionText });
    } else {
      // Custom dropdown: click to open, then select the option
      await selectLocator.click();
      await page.waitForTimeout(300);
      const option = page.getByRole('option', { name: optionText })
        .or(page.getByText(optionText, { exact: false }));
      await option.first().click();
    }
  } else {
    // Fallback: try data-testid based dropdown
    const nameStr = typeof label === 'string' ? label : label.source;
    const fallback = page.locator(
      `[data-testid="field-${nameStr.toLowerCase().replace(/\s+/g, '-')}"]`,
    );
    const fallbackVisible = await fallback.first().isVisible().catch(() => false);
    if (fallbackVisible) {
      await fallback.first().click();
      await page.waitForTimeout(300);
      const option = page.getByRole('option', { name: optionText })
        .or(page.getByText(optionText, { exact: false }));
      await option.first().click();
    }
  }
}

// ---------------------------------------------------------------------------
// Test Suite
// ---------------------------------------------------------------------------

test.describe('CRM', () => {
  /**
   * Run tests serially within the CRM suite because account/contact
   * creation tests produce side effects consumed by subsequent relation
   * and search tests. Serial execution avoids race conditions and ensures
   * deterministic data availability.
   */
  test.describe.configure({ mode: 'serial' });

  let context: BrowserContext;
  let page: Page;

  // Tracked resource names/IDs for cleanup and cross-test data sharing
  const createdAccountNames: string[] = [];
  const createdContactNames: string[] = [];

  // Pre-generated unique names for test resources
  const testAccountName = uniqueName('Acme Corp');
  const testAccountNameEdited = uniqueName('Acme Global');
  const testContactFirstName = uniqueName('John');
  const testContactLastName = uniqueName('Doe');
  const testContactFirstNameEdited = uniqueName('Jane');
  const testContactEmail = `testcontact.${RUN_ID}@webvella-test.com`;
  const testContactEmailEdited = `edited.${RUN_ID}@webvella-test.com`;
  const testAccountEmail = `testaccount.${RUN_ID}@webvella-test.com`;
  const testAccountEmailEdited = `editedaccount.${RUN_ID}@webvella-test.com`;
  const testAccountWebsite = `https://acme-${RUN_ID}.example.com`;
  const testAccountCity = `TestCity${RUN_ID}`;
  const testAccountPhone = '+1-555-0100';
  const testContactJobTitle = `Engineer ${RUN_ID}`;
  const testContactPhone = '+1-555-0200';

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

    // Authenticate as admin using the shared login helper from auth.spec.ts
    await login(page);
  });

  /**
   * After all tests: best-effort cleanup of all CRM resources created during
   * the run, then close the browser context. Cleanup runs in reverse creation
   * order to respect referential constraints (contacts before accounts if linked).
   */
  test.afterAll(async () => {
    if (page && !page.isClosed()) {
      // Clean up contacts first (may be linked to accounts)
      for (const name of [...createdContactNames].reverse()) {
        await cleanupResource(page, CONTACTS_URL, name);
      }
      // Clean up accounts
      for (const name of [...createdAccountNames].reverse()) {
        await cleanupResource(page, ACCOUNTS_URL, name);
      }
    }

    if (context) {
      await context.close();
    }
  });

  /**
   * Before each test: ensure DOM is settled from previous test navigation.
   * Individual tests navigate to their specific starting routes.
   */
  test.beforeEach(async () => {
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(SETTLE_TIME);
  });

  // =========================================================================
  // Account CRUD Tests
  // =========================================================================

  test.describe('Account CRUD', () => {
    /**
     * Replaces: NextPlugin.20190204.cs account entity workflows
     * Source entity ID: 2e22b50f-e444-4b62-a171-076e51246939
     * Key fields: name (required), type (select: Company/Person),
     *   website, email, mobile_phone, city, salutation_id
     */

    test('should display the account list page with data table', async () => {
      // Navigate to the accounts list page
      await page.goto(ACCOUNTS_URL, {
        waitUntil: 'networkidle',
        timeout: DATA_TIMEOUT,
      });
      await page.waitForTimeout(SETTLE_TIME);

      // Assert: the page renders with recognizable CRM account content
      // Look for account-related heading or breadcrumb
      const heading = page
        .getByRole('heading', { name: /account/i })
        .or(page.getByText(/accounts/i).first());
      await expect(heading.first()).toBeVisible({ timeout: DATA_TIMEOUT });

      // Assert: a data table or list structure is present
      const dataTable = page
        .getByRole('table')
        .or(page.locator('[data-testid="data-table"]'))
        .or(page.locator('[data-testid="account-list"]'))
        .or(page.locator('.data-table'));
      await expect(dataTable.first()).toBeVisible({ timeout: DATA_TIMEOUT });

      // Assert: column headers for key account fields are present
      // The data table should show at least: name, type, email
      const nameHeader = page
        .getByRole('columnheader', { name: /name/i })
        .or(page.locator('th').filter({ hasText: /name/i }));
      await expect(nameHeader.first()).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    test('should create a new account with required and optional fields', async () => {
      // Navigate to the account creation page
      await page.goto(`${ACCOUNTS_URL}/create`, {
        waitUntil: 'networkidle',
        timeout: DATA_TIMEOUT,
      });
      await page.waitForTimeout(SETTLE_TIME);

      // Fill in the required "name" field
      await fillField(page, /name/i, testAccountName);

      // Select account type — "Company" (value "1" in monolith's select options)
      // The React SPA should render this as a <select> or custom dropdown
      // with options "Company" and "Person"
      const typeField = page
        .getByLabel(/type/i)
        .or(page.locator('[data-testid="field-type"]'));
      const typeVisible = await typeField.first().isVisible().catch(() => false);
      if (typeVisible) {
        const tagName = await typeField.first().evaluate((el) => el.tagName.toLowerCase());
        if (tagName === 'select') {
          // Native select: try selecting by visible text
          await typeField.first().selectOption({ label: 'Company' }).catch(async () => {
            // Fallback: try by value
            await typeField.first().selectOption('1');
          });
        } else {
          // Custom dropdown: click to open, select "Company"
          await typeField.first().click();
          await page.waitForTimeout(300);
          const companyOption = page
            .getByRole('option', { name: /company/i })
            .or(page.getByText('Company', { exact: true }));
          await companyOption.first().click();
        }
      }

      // Fill optional fields: website, email, mobile_phone, city
      await fillField(page, /website/i, testAccountWebsite);
      await fillField(page, /email/i, testAccountEmail);
      await fillField(page, /mobile.?phone/i, testAccountPhone);
      await fillField(page, /city/i, testAccountCity);

      // Submit the form
      const submitBtn = page
        .getByRole('button', { name: /save|create|submit/i })
        .or(page.locator('[data-testid="submit-btn"]'))
        .or(page.locator('button[type="submit"]'));
      await submitBtn.first().click();

      // Wait for the API call to complete and redirect
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(SETTLE_TIME);

      // Track the created account for cleanup
      createdAccountNames.push(testAccountName);

      // Assert: redirect to account details or back to account list
      const currentUrl = page.url();
      const redirectedCorrectly =
        currentUrl.includes('/crm/accounts/') || currentUrl.includes(ACCOUNTS_URL);
      expect(redirectedCorrectly).toBeTruthy();

      // Assert: the new account is visible — check details page or navigate to list
      if (!currentUrl.endsWith('/accounts') && !currentUrl.endsWith('/accounts/')) {
        // We're on a details page — verify account name is displayed
        const accountNameText = page.getByText(testAccountName, { exact: false });
        await expect(accountNameText.first()).toBeVisible({ timeout: DATA_TIMEOUT });
      } else {
        // We're on the list page — verify account appears in the list
        const accountInList = page.getByText(testAccountName, { exact: false });
        await expect(accountInList.first()).toBeVisible({ timeout: DATA_TIMEOUT });
      }
    });

    test('should verify the created account appears in the account list', async () => {
      // Navigate to the accounts list page
      await page.goto(ACCOUNTS_URL, {
        waitUntil: 'networkidle',
        timeout: DATA_TIMEOUT,
      });
      await page.waitForTimeout(SETTLE_TIME);

      // Assert: the newly created account appears in the data table
      const accountRow = page.getByText(testAccountName, { exact: false });
      await expect(accountRow.first()).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    test('should edit an existing account', async () => {
      // Navigate to the accounts list and find the created account
      await page.goto(ACCOUNTS_URL, {
        waitUntil: 'networkidle',
        timeout: DATA_TIMEOUT,
      });
      await page.waitForTimeout(SETTLE_TIME);

      // Click on the account to navigate to its details
      const accountLink = page.getByText(testAccountName, { exact: false });
      await expect(accountLink.first()).toBeVisible({ timeout: DATA_TIMEOUT });
      await accountLink.first().click();
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(SETTLE_TIME);

      // Navigate to the edit page — try edit button or direct URL
      const editBtn = page
        .getByRole('link', { name: /edit/i })
        .or(page.getByRole('button', { name: /edit/i }))
        .or(page.locator('[data-testid="edit-btn"]'))
        .or(page.locator('a[href*="/edit"]'));

      const editVisible = await editBtn.first().isVisible().catch(() => false);
      if (editVisible) {
        await editBtn.first().click();
        await page.waitForLoadState('networkidle');
        await page.waitForTimeout(SETTLE_TIME);
      } else {
        // Fallback: try appending /edit to current URL
        const currentUrl = page.url();
        if (!currentUrl.endsWith('/edit')) {
          await page.goto(`${currentUrl}/edit`, {
            waitUntil: 'networkidle',
            timeout: DATA_TIMEOUT,
          });
          await page.waitForTimeout(SETTLE_TIME);
        }
      }

      // Modify account fields: name, email
      const nameField = page
        .getByLabel(/name/i)
        .or(page.getByRole('textbox', { name: /name/i }))
        .or(page.locator('[data-testid="field-name"]'));
      await nameField.first().clear();
      await nameField.first().fill(testAccountNameEdited);

      // Update email
      const emailField = page
        .getByLabel(/email/i)
        .or(page.getByRole('textbox', { name: /email/i }))
        .or(page.locator('[data-testid="field-email"]'));
      const emailVisible = await emailField.first().isVisible().catch(() => false);
      if (emailVisible) {
        await emailField.first().clear();
        await emailField.first().fill(testAccountEmailEdited);
      }

      // Change type from Company to Person
      const typeField = page
        .getByLabel(/type/i)
        .or(page.locator('[data-testid="field-type"]'));
      const typeVisible = await typeField.first().isVisible().catch(() => false);
      if (typeVisible) {
        const tagName = await typeField.first().evaluate((el) => el.tagName.toLowerCase());
        if (tagName === 'select') {
          await typeField.first().selectOption({ label: 'Person' }).catch(async () => {
            await typeField.first().selectOption('2');
          });
        } else {
          await typeField.first().click();
          await page.waitForTimeout(300);
          const personOption = page
            .getByRole('option', { name: /person/i })
            .or(page.getByText('Person', { exact: true }));
          await personOption.first().click();
        }
      }

      // Save changes
      const saveBtn = page
        .getByRole('button', { name: /save|update|submit/i })
        .or(page.locator('[data-testid="submit-btn"]'))
        .or(page.locator('button[type="submit"]'));
      await saveBtn.first().click();
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(SETTLE_TIME);

      // Update tracked name for cleanup
      const nameIndex = createdAccountNames.indexOf(testAccountName);
      if (nameIndex >= 0) {
        createdAccountNames[nameIndex] = testAccountNameEdited;
      } else {
        createdAccountNames.push(testAccountNameEdited);
      }

      // Assert: redirect to details or list with updated values
      const currentUrl = page.url();
      const redirectedCorrectly =
        currentUrl.includes('/crm/accounts/') || currentUrl.includes(ACCOUNTS_URL);
      expect(redirectedCorrectly).toBeTruthy();

      // Assert: the updated account name is displayed
      const updatedName = page.getByText(testAccountNameEdited, { exact: false });
      await expect(updatedName.first()).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    test('should delete an existing account', async () => {
      // First, create a separate account specifically for deletion testing
      const deleteAccountName = uniqueName('DeleteMe Corp');

      await page.goto(`${ACCOUNTS_URL}/create`, {
        waitUntil: 'networkidle',
        timeout: DATA_TIMEOUT,
      });
      await page.waitForTimeout(SETTLE_TIME);

      // Fill required name field
      await fillField(page, /name/i, deleteAccountName);

      // Submit the creation form
      const submitBtn = page
        .getByRole('button', { name: /save|create|submit/i })
        .or(page.locator('[data-testid="submit-btn"]'))
        .or(page.locator('button[type="submit"]'));
      await submitBtn.first().click();
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(SETTLE_TIME);

      // Navigate to the account details page (if not already there)
      const currentUrl = page.url();
      if (currentUrl.endsWith('/accounts') || currentUrl.endsWith('/accounts/')) {
        // Navigate from list to details by clicking the account name
        const accountLink = page.getByText(deleteAccountName, { exact: false });
        await expect(accountLink.first()).toBeVisible({ timeout: DATA_TIMEOUT });
        await accountLink.first().click();
        await page.waitForLoadState('networkidle');
        await page.waitForTimeout(SETTLE_TIME);
      }

      // Click the delete button on the details page
      const deleteBtn = page
        .getByRole('button', { name: /delete/i })
        .or(page.locator('[data-testid="delete-btn"]'))
        .or(page.locator('button[aria-label*="delete" i]'));
      await expect(deleteBtn.first()).toBeVisible({ timeout: DATA_TIMEOUT });
      await deleteBtn.first().click();
      await page.waitForTimeout(SETTLE_TIME);

      // Confirm the deletion in the confirmation dialog/modal
      const confirmBtn = page
        .getByRole('button', { name: /confirm|yes|ok|delete/i })
        .or(page.locator('[data-testid="confirm-delete-btn"]'));
      const confirmVisible = await confirmBtn.first().isVisible().catch(() => false);
      if (confirmVisible) {
        await confirmBtn.first().click();
      }

      // Wait for redirect after deletion
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(SETTLE_TIME);

      // Assert: redirected to the account list page
      await expect(page).toHaveURL(/\/crm\/accounts/i, { timeout: DATA_TIMEOUT });

      // Assert: the deleted account no longer appears in the list
      await page.goto(ACCOUNTS_URL, {
        waitUntil: 'networkidle',
        timeout: DATA_TIMEOUT,
      });
      await page.waitForTimeout(SETTLE_TIME);

      const deletedAccount = page.getByText(deleteAccountName, { exact: true });
      await expect(deletedAccount).toHaveCount(0, { timeout: DATA_TIMEOUT });
    });
  });

  // =========================================================================
  // Contact CRUD Tests
  // =========================================================================

  test.describe('Contact CRUD', () => {
    /**
     * Replaces: NextPlugin.20190204.cs + NextPlugin.20190206.cs contact workflows
     * Source entity ID: 39e1dd9b-827f-464d-95ea-507ade81cbd0
     * Key fields: first_name (required), last_name (required), salutation_id
     *   (required, default Mr.), email, job_title, mobile_phone, photo
     * Salutation seeded values: Mr., Ms., Mrs., Dr., Prof.
     */

    test('should display the contact list page with data table', async () => {
      // Navigate to the contacts list page
      await page.goto(CONTACTS_URL, {
        waitUntil: 'networkidle',
        timeout: DATA_TIMEOUT,
      });
      await page.waitForTimeout(SETTLE_TIME);

      // Assert: the page renders with recognizable CRM contact content
      const heading = page
        .getByRole('heading', { name: /contact/i })
        .or(page.getByText(/contacts/i).first());
      await expect(heading.first()).toBeVisible({ timeout: DATA_TIMEOUT });

      // Assert: a data table or list structure is present
      const dataTable = page
        .getByRole('table')
        .or(page.locator('[data-testid="data-table"]'))
        .or(page.locator('[data-testid="contact-list"]'))
        .or(page.locator('.data-table'));
      await expect(dataTable.first()).toBeVisible({ timeout: DATA_TIMEOUT });

      // Assert: column headers for key contact fields are present
      const nameHeader = page
        .getByRole('columnheader', { name: /name/i })
        .or(page.locator('th').filter({ hasText: /name/i }));
      await expect(nameHeader.first()).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    test('should create a new contact with required and optional fields', async () => {
      // Navigate to the contact creation page
      await page.goto(`${CONTACTS_URL}/create`, {
        waitUntil: 'networkidle',
        timeout: DATA_TIMEOUT,
      });
      await page.waitForTimeout(SETTLE_TIME);

      // Fill in the required "first_name" field
      await fillField(page, /first.?name/i, testContactFirstName);

      // Fill in the required "last_name" field
      await fillField(page, /last.?name/i, testContactLastName);

      // Select salutation — "Mr." is the default (ID: 87c08ee1-8d4d-4c89-9b37-4e3cc3f98698)
      // Salutation values seeded in NextPlugin.20190206.cs: Mr., Ms., Mrs., Dr., Prof.
      const salutationField = page
        .getByLabel(/salutation/i)
        .or(page.locator('[data-testid="field-salutation"]'))
        .or(page.locator('[data-testid="field-salutation-id"]'));
      const salutationVisible = await salutationField.first().isVisible().catch(() => false);
      if (salutationVisible) {
        const tagName = await salutationField
          .first()
          .evaluate((el) => el.tagName.toLowerCase());
        if (tagName === 'select') {
          // Native select: pick "Dr." to test non-default selection
          await salutationField.first().selectOption({ label: 'Dr.' }).catch(async () => {
            // Fallback: try partial label match
            const options = await salutationField.first().locator('option').allTextContents();
            const drOption = options.find((opt) => opt.includes('Dr'));
            if (drOption) {
              await salutationField.first().selectOption({ label: drOption });
            }
          });
        } else {
          // Custom dropdown: click to open, select "Dr."
          await salutationField.first().click();
          await page.waitForTimeout(300);
          const drOption = page
            .getByRole('option', { name: /dr\.?/i })
            .or(page.getByText('Dr.', { exact: false }));
          await drOption.first().click();
        }
      }

      // Fill optional fields: email, job_title, mobile_phone
      await fillField(page, /email/i, testContactEmail);
      await fillField(page, /job.?title/i, testContactJobTitle);
      await fillField(page, /mobile.?phone/i, testContactPhone);

      // Submit the form
      const submitBtn = page
        .getByRole('button', { name: /save|create|submit/i })
        .or(page.locator('[data-testid="submit-btn"]'))
        .or(page.locator('button[type="submit"]'));
      await submitBtn.first().click();

      // Wait for the API call to complete and redirect
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(SETTLE_TIME);

      // Track the created contact for cleanup
      createdContactNames.push(testContactFirstName);
      createdContactNames.push(testContactLastName);

      // Assert: redirect to contact details or list
      const currentUrl = page.url();
      const redirectedCorrectly =
        currentUrl.includes('/crm/contacts/') || currentUrl.includes(CONTACTS_URL);
      expect(redirectedCorrectly).toBeTruthy();

      // Assert: the new contact name is visible
      const contactName = page.getByText(testContactFirstName, { exact: false });
      await expect(contactName.first()).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    test('should verify the created contact appears in the contact list', async () => {
      // Navigate to the contacts list page
      await page.goto(CONTACTS_URL, {
        waitUntil: 'networkidle',
        timeout: DATA_TIMEOUT,
      });
      await page.waitForTimeout(SETTLE_TIME);

      // Assert: the newly created contact appears in the data table
      // Check for first name or combined name
      const contactRow = page
        .getByText(testContactFirstName, { exact: false })
        .or(page.getByText(testContactLastName, { exact: false }));
      await expect(contactRow.first()).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    test('should edit an existing contact', async () => {
      // Navigate to the contacts list and find the created contact
      await page.goto(CONTACTS_URL, {
        waitUntil: 'networkidle',
        timeout: DATA_TIMEOUT,
      });
      await page.waitForTimeout(SETTLE_TIME);

      // Click on the contact to navigate to its details
      const contactLink = page
        .getByText(testContactFirstName, { exact: false })
        .or(page.getByText(testContactLastName, { exact: false }));
      await expect(contactLink.first()).toBeVisible({ timeout: DATA_TIMEOUT });
      await contactLink.first().click();
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(SETTLE_TIME);

      // Navigate to the edit page
      const editBtn = page
        .getByRole('link', { name: /edit/i })
        .or(page.getByRole('button', { name: /edit/i }))
        .or(page.locator('[data-testid="edit-btn"]'))
        .or(page.locator('a[href*="/edit"]'));

      const editVisible = await editBtn.first().isVisible().catch(() => false);
      if (editVisible) {
        await editBtn.first().click();
        await page.waitForLoadState('networkidle');
        await page.waitForTimeout(SETTLE_TIME);
      } else {
        // Fallback: try appending /edit to current URL
        const currentUrl = page.url();
        if (!currentUrl.endsWith('/edit')) {
          await page.goto(`${currentUrl}/edit`, {
            waitUntil: 'networkidle',
            timeout: DATA_TIMEOUT,
          });
          await page.waitForTimeout(SETTLE_TIME);
        }
      }

      // Modify contact fields: first_name, email
      const firstNameField = page
        .getByLabel(/first.?name/i)
        .or(page.getByRole('textbox', { name: /first.?name/i }))
        .or(page.locator('[data-testid="field-first-name"]'));
      await firstNameField.first().clear();
      await firstNameField.first().fill(testContactFirstNameEdited);

      // Update email
      const emailField = page
        .getByLabel(/email/i)
        .or(page.getByRole('textbox', { name: /email/i }))
        .or(page.locator('[data-testid="field-email"]'));
      const emailVisible = await emailField.first().isVisible().catch(() => false);
      if (emailVisible) {
        await emailField.first().clear();
        await emailField.first().fill(testContactEmailEdited);
      }

      // Change salutation from Dr. to Prof. to test salutation editing
      // (NextPlugin.20190206.cs seeded values: Mr., Ms., Mrs., Dr., Prof.)
      const salutationField = page
        .getByLabel(/salutation/i)
        .or(page.locator('[data-testid="field-salutation"]'))
        .or(page.locator('[data-testid="field-salutation-id"]'));
      const salutationVisible = await salutationField.first().isVisible().catch(() => false);
      if (salutationVisible) {
        const tagName = await salutationField
          .first()
          .evaluate((el) => el.tagName.toLowerCase());
        if (tagName === 'select') {
          await salutationField.first().selectOption({ label: 'Prof.' }).catch(async () => {
            const options = await salutationField.first().locator('option').allTextContents();
            const profOption = options.find((opt) => opt.includes('Prof'));
            if (profOption) {
              await salutationField.first().selectOption({ label: profOption });
            }
          });
        } else {
          await salutationField.first().click();
          await page.waitForTimeout(300);
          const profOption = page
            .getByRole('option', { name: /prof\.?/i })
            .or(page.getByText('Prof.', { exact: false }));
          await profOption.first().click();
        }
      }

      // Save changes
      const saveBtn = page
        .getByRole('button', { name: /save|update|submit/i })
        .or(page.locator('[data-testid="submit-btn"]'))
        .or(page.locator('button[type="submit"]'));
      await saveBtn.first().click();
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(SETTLE_TIME);

      // Update tracked names for cleanup
      const firstNameIdx = createdContactNames.indexOf(testContactFirstName);
      if (firstNameIdx >= 0) {
        createdContactNames[firstNameIdx] = testContactFirstNameEdited;
      } else {
        createdContactNames.push(testContactFirstNameEdited);
      }

      // Assert: redirect to details or list with updated values
      const currentUrl = page.url();
      const redirectedCorrectly =
        currentUrl.includes('/crm/contacts/') || currentUrl.includes(CONTACTS_URL);
      expect(redirectedCorrectly).toBeTruthy();

      // Assert: the updated contact first name is displayed
      const updatedName = page.getByText(testContactFirstNameEdited, { exact: false });
      await expect(updatedName.first()).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    test('should delete an existing contact', async () => {
      // Create a separate contact specifically for deletion testing
      const deleteContactFirst = uniqueName('DelFirst');
      const deleteContactLast = uniqueName('DelLast');

      await page.goto(`${CONTACTS_URL}/create`, {
        waitUntil: 'networkidle',
        timeout: DATA_TIMEOUT,
      });
      await page.waitForTimeout(SETTLE_TIME);

      // Fill required fields
      await fillField(page, /first.?name/i, deleteContactFirst);
      await fillField(page, /last.?name/i, deleteContactLast);

      // Submit the creation form
      const submitBtn = page
        .getByRole('button', { name: /save|create|submit/i })
        .or(page.locator('[data-testid="submit-btn"]'))
        .or(page.locator('button[type="submit"]'));
      await submitBtn.first().click();
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(SETTLE_TIME);

      // Navigate to the contact details page (if not already there)
      const currentUrl = page.url();
      if (currentUrl.endsWith('/contacts') || currentUrl.endsWith('/contacts/')) {
        const contactLink = page.getByText(deleteContactFirst, { exact: false });
        await expect(contactLink.first()).toBeVisible({ timeout: DATA_TIMEOUT });
        await contactLink.first().click();
        await page.waitForLoadState('networkidle');
        await page.waitForTimeout(SETTLE_TIME);
      }

      // Click the delete button on the details page
      const deleteBtn = page
        .getByRole('button', { name: /delete/i })
        .or(page.locator('[data-testid="delete-btn"]'))
        .or(page.locator('button[aria-label*="delete" i]'));
      await expect(deleteBtn.first()).toBeVisible({ timeout: DATA_TIMEOUT });
      await deleteBtn.first().click();
      await page.waitForTimeout(SETTLE_TIME);

      // Confirm the deletion
      const confirmBtn = page
        .getByRole('button', { name: /confirm|yes|ok|delete/i })
        .or(page.locator('[data-testid="confirm-delete-btn"]'));
      const confirmVisible = await confirmBtn.first().isVisible().catch(() => false);
      if (confirmVisible) {
        await confirmBtn.first().click();
      }

      // Wait for redirect
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(SETTLE_TIME);

      // Assert: redirected to the contact list page
      await expect(page).toHaveURL(/\/crm\/contacts/i, { timeout: DATA_TIMEOUT });

      // Assert: the deleted contact no longer appears in the list
      await page.goto(CONTACTS_URL, {
        waitUntil: 'networkidle',
        timeout: DATA_TIMEOUT,
      });
      await page.waitForTimeout(SETTLE_TIME);

      const deletedContact = page.getByText(deleteContactFirst, { exact: true });
      await expect(deletedContact).toHaveCount(0, { timeout: DATA_TIMEOUT });
    });
  });

  // =========================================================================
  // Account-Contact Relations Tests
  // =========================================================================

  test.describe('Account-Contact Relations', () => {
    /**
     * Replaces: account_nn_contact ManyToMany relation from NextPlugin.20190204.cs
     * Relation ID: dd211c99-5415-4195-923a-cb5a56e5d544
     * Relation type: ManyToMany (origin: account.id, target: contact.id)
     *
     * In the monolith, related records were managed through:
     *   RecordRelatedRecordsList.cshtml — display linked records
     *   RecordRelatedRecordCreate.cshtml — add new related record
     *   RecordRelatedRecordManage.cshtml — manage relation
     *
     * The React SPA replaces these with inline tabs/sections on the
     * account details page, with link/unlink actions via API calls.
     */

    test('should link a contact to an account', async () => {
      // Navigate to the edited account's details page
      await page.goto(ACCOUNTS_URL, {
        waitUntil: 'networkidle',
        timeout: DATA_TIMEOUT,
      });
      await page.waitForTimeout(SETTLE_TIME);

      // Find and click the test account (edited name)
      const accountLink = page.getByText(testAccountNameEdited, { exact: false });
      await expect(accountLink.first()).toBeVisible({ timeout: DATA_TIMEOUT });
      await accountLink.first().click();
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(SETTLE_TIME);

      // Look for the related contacts section / tab
      // The React SPA should have a tab/section for "Contacts" or "Related Contacts"
      const contactsTab = page
        .getByRole('tab', { name: /contact/i })
        .or(page.getByRole('link', { name: /contact/i }))
        .or(page.getByText(/related contacts/i))
        .or(page.locator('[data-testid="related-contacts-tab"]'))
        .or(page.locator('[data-testid="contacts-tab"]'));

      const tabVisible = await contactsTab.first().isVisible().catch(() => false);
      if (tabVisible) {
        await contactsTab.first().click();
        await page.waitForTimeout(SETTLE_TIME);
      }

      // Click "Add Contact" or "Link Contact" button to associate an existing contact
      const addContactBtn = page
        .getByRole('button', { name: /add contact|link contact|associate/i })
        .or(page.locator('[data-testid="add-related-contact"]'))
        .or(page.locator('[data-testid="link-contact-btn"]'))
        .or(page.getByRole('button', { name: /add|link/i }));
      await expect(addContactBtn.first()).toBeVisible({ timeout: DATA_TIMEOUT });
      await addContactBtn.first().click();
      await page.waitForTimeout(SETTLE_TIME);

      // A modal/dialog or dropdown should appear to select a contact
      // Search for or select the test contact by its edited first name
      const searchInput = page
        .getByRole('searchbox')
        .or(page.getByPlaceholder(/search/i))
        .or(page.locator('[data-testid="contact-search-input"]'))
        .or(page.getByRole('textbox'));
      const searchVisible = await searchInput.first().isVisible().catch(() => false);
      if (searchVisible) {
        await searchInput.first().fill(testContactFirstNameEdited);
        await page.waitForTimeout(SETTLE_TIME);
      }

      // Select the contact from the results
      const contactOption = page.getByText(testContactFirstNameEdited, { exact: false });
      await expect(contactOption.first()).toBeVisible({ timeout: DATA_TIMEOUT });
      await contactOption.first().click();
      await page.waitForTimeout(SETTLE_TIME);

      // Confirm the link if a confirmation button is present
      const confirmLinkBtn = page
        .getByRole('button', { name: /confirm|save|link|add|ok/i })
        .or(page.locator('[data-testid="confirm-link-btn"]'));
      const confirmLinkVisible = await confirmLinkBtn.first().isVisible().catch(() => false);
      if (confirmLinkVisible) {
        await confirmLinkBtn.first().click();
        await page.waitForLoadState('networkidle');
        await page.waitForTimeout(SETTLE_TIME);
      }

      // Assert: the linked contact appears in the related contacts section
      const linkedContact = page.getByText(testContactFirstNameEdited, { exact: false });
      await expect(linkedContact.first()).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    test('should unlink a contact from an account', async () => {
      // Navigate to the account details page
      await page.goto(ACCOUNTS_URL, {
        waitUntil: 'networkidle',
        timeout: DATA_TIMEOUT,
      });
      await page.waitForTimeout(SETTLE_TIME);

      // Click the test account
      const accountLink = page.getByText(testAccountNameEdited, { exact: false });
      await expect(accountLink.first()).toBeVisible({ timeout: DATA_TIMEOUT });
      await accountLink.first().click();
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(SETTLE_TIME);

      // Navigate to the related contacts tab/section
      const contactsTab = page
        .getByRole('tab', { name: /contact/i })
        .or(page.getByRole('link', { name: /contact/i }))
        .or(page.getByText(/related contacts/i))
        .or(page.locator('[data-testid="related-contacts-tab"]'))
        .or(page.locator('[data-testid="contacts-tab"]'));

      const tabVisible = await contactsTab.first().isVisible().catch(() => false);
      if (tabVisible) {
        await contactsTab.first().click();
        await page.waitForTimeout(SETTLE_TIME);
      }

      // Verify the linked contact is visible before unlinking
      const linkedContact = page.getByText(testContactFirstNameEdited, { exact: false });
      await expect(linkedContact.first()).toBeVisible({ timeout: DATA_TIMEOUT });

      // Click the unlink/remove button for the linked contact
      // Look for an unlink action near the contact entry
      const unlinkBtn = page
        .getByRole('button', { name: /unlink|remove|disconnect/i })
        .or(page.locator('[data-testid="unlink-contact-btn"]'))
        .or(page.locator('[data-testid="remove-relation-btn"]'))
        .or(page.locator('button[aria-label*="unlink" i]'))
        .or(page.locator('button[aria-label*="remove" i]'));
      await expect(unlinkBtn.first()).toBeVisible({ timeout: DATA_TIMEOUT });
      await unlinkBtn.first().click();
      await page.waitForTimeout(SETTLE_TIME);

      // Confirm the unlink action if a confirmation dialog appears
      const confirmBtn = page
        .getByRole('button', { name: /confirm|yes|ok|unlink|remove/i })
        .or(page.locator('[data-testid="confirm-unlink-btn"]'));
      const confirmVisible = await confirmBtn.first().isVisible().catch(() => false);
      if (confirmVisible) {
        await confirmBtn.first().click();
        await page.waitForLoadState('networkidle');
        await page.waitForTimeout(SETTLE_TIME);
      }

      // Assert: the contact is removed from the related contacts list
      // Use a fresh page load to ensure the unlink was persisted
      await page.reload({ waitUntil: 'networkidle' });
      await page.waitForTimeout(SETTLE_TIME);

      // Re-navigate to the related contacts tab if needed
      const reloadTab = page
        .getByRole('tab', { name: /contact/i })
        .or(page.locator('[data-testid="related-contacts-tab"]'))
        .or(page.locator('[data-testid="contacts-tab"]'));
      const reloadTabVisible = await reloadTab.first().isVisible().catch(() => false);
      if (reloadTabVisible) {
        await reloadTab.first().click();
        await page.waitForTimeout(SETTLE_TIME);
      }

      // Assert: the contact should no longer appear in the related contacts section
      // It should still exist independently (just unlinked from this account)
      const unlinkedContact = page.getByText(testContactFirstNameEdited, { exact: true });
      // Either the contact text is completely gone, or it's hidden within this section
      const stillVisible = await unlinkedContact.isVisible().catch(() => false);
      // The contact should no longer be in the related contacts section
      // (it may still appear elsewhere on the page in other contexts)
      if (stillVisible) {
        // If still visible, it might be in a different context — verify the related
        // contacts section specifically doesn't contain it
        const relatedSection = page
          .locator('[data-testid="related-contacts-section"]')
          .or(page.locator('[role="tabpanel"]'))
          .or(page.locator('.related-contacts'));
        const sectionVisible = await relatedSection.first().isVisible().catch(() => false);
        if (sectionVisible) {
          const contactInSection = relatedSection
            .first()
            .getByText(testContactFirstNameEdited, { exact: true });
          await expect(contactInSection).toHaveCount(0, { timeout: DATA_TIMEOUT });
        }
      }

      // Verify the contact still exists independently in the contacts list
      await page.goto(CONTACTS_URL, {
        waitUntil: 'networkidle',
        timeout: DATA_TIMEOUT,
      });
      await page.waitForTimeout(SETTLE_TIME);

      const independentContact = page.getByText(testContactFirstNameEdited, { exact: false });
      await expect(independentContact.first()).toBeVisible({ timeout: DATA_TIMEOUT });
    });
  });

  // =========================================================================
  // CRM Search Tests
  // =========================================================================

  test.describe('CRM Search', () => {
    /**
     * Replaces: SearchService.cs x_search field indexing and Configuration.cs
     *   search index field definitions.
     *
     * The monolith's SearchService maintained an x_search text field on each
     * account/contact record by concatenating indexed field values on post-create
     * and post-update hooks (AccountHook.cs, ContactHook.cs).
     *
     * AccountSearchIndexFields (Configuration.cs): city, country, email,
     *   fax_phone, first_name, fixed_phone, last_name, mobile_phone, name,
     *   notes, post_code, region, street, street_2, tax_id, type, website.
     *
     * ContactSearchIndexFields (Configuration.cs): city, country, account name,
     *   email, fax_phone, first_name, fixed_phone, job_title, last_name,
     *   mobile_phone, notes, post_code, region, street, street_2.
     *
     * In the React SPA, search is exposed via:
     *   - A search/filter input on the CRM pages (accounts, contacts)
     *   - A global CRM search that queries across both entity types
     *   - The x_search field is maintained server-side by the CRM Lambda handler
     */

    test('should search across accounts by name', async () => {
      // Navigate to the accounts list page
      await page.goto(ACCOUNTS_URL, {
        waitUntil: 'networkidle',
        timeout: DATA_TIMEOUT,
      });
      await page.waitForTimeout(SETTLE_TIME);

      // Find the search/filter input on the accounts page
      const searchInput = page
        .getByRole('searchbox')
        .or(page.getByPlaceholder(/search/i))
        .or(page.locator('[data-testid="search-input"]'))
        .or(page.locator('[data-testid="filter-input"]'))
        .or(page.getByLabel(/search/i));

      await expect(searchInput.first()).toBeVisible({ timeout: DATA_TIMEOUT });

      // Search for the test account by its edited name
      // Extract a distinctive portion of the name for reliable matching
      await searchInput.first().fill(testAccountNameEdited);
      await page.waitForTimeout(SETTLE_TIME);

      // Wait for search results to update (debounced API call)
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(SETTLE_TIME);

      // Assert: the matching account appears in the filtered results
      const matchingAccount = page.getByText(testAccountNameEdited, { exact: false });
      await expect(matchingAccount.first()).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    test('should search across accounts by email', async () => {
      // Navigate to accounts list
      await page.goto(ACCOUNTS_URL, {
        waitUntil: 'networkidle',
        timeout: DATA_TIMEOUT,
      });
      await page.waitForTimeout(SETTLE_TIME);

      // Find the search input
      const searchInput = page
        .getByRole('searchbox')
        .or(page.getByPlaceholder(/search/i))
        .or(page.locator('[data-testid="search-input"]'))
        .or(page.locator('[data-testid="filter-input"]'))
        .or(page.getByLabel(/search/i));

      await expect(searchInput.first()).toBeVisible({ timeout: DATA_TIMEOUT });

      // Search by the account email (email is an indexed field in Configuration.cs)
      await searchInput.first().fill(testAccountEmailEdited);
      await page.waitForTimeout(SETTLE_TIME);
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(SETTLE_TIME);

      // Assert: the matching account appears based on email search
      // The x_search field should contain the email value
      const matchingAccount = page.getByText(testAccountNameEdited, { exact: false });
      await expect(matchingAccount.first()).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    test('should search across contacts by name', async () => {
      // Navigate to the contacts list page
      await page.goto(CONTACTS_URL, {
        waitUntil: 'networkidle',
        timeout: DATA_TIMEOUT,
      });
      await page.waitForTimeout(SETTLE_TIME);

      // Find the search input
      const searchInput = page
        .getByRole('searchbox')
        .or(page.getByPlaceholder(/search/i))
        .or(page.locator('[data-testid="search-input"]'))
        .or(page.locator('[data-testid="filter-input"]'))
        .or(page.getByLabel(/search/i));

      await expect(searchInput.first()).toBeVisible({ timeout: DATA_TIMEOUT });

      // Search for the test contact by its edited first name
      await searchInput.first().fill(testContactFirstNameEdited);
      await page.waitForTimeout(SETTLE_TIME);
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(SETTLE_TIME);

      // Assert: the matching contact appears in the filtered results
      const matchingContact = page.getByText(testContactFirstNameEdited, { exact: false });
      await expect(matchingContact.first()).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    test('should search across contacts by email', async () => {
      // Navigate to contacts list
      await page.goto(CONTACTS_URL, {
        waitUntil: 'networkidle',
        timeout: DATA_TIMEOUT,
      });
      await page.waitForTimeout(SETTLE_TIME);

      // Find the search input
      const searchInput = page
        .getByRole('searchbox')
        .or(page.getByPlaceholder(/search/i))
        .or(page.locator('[data-testid="search-input"]'))
        .or(page.locator('[data-testid="filter-input"]'))
        .or(page.getByLabel(/search/i));

      await expect(searchInput.first()).toBeVisible({ timeout: DATA_TIMEOUT });

      // Search by the contact email (email is an indexed field in Configuration.cs)
      await searchInput.first().fill(testContactEmailEdited);
      await page.waitForTimeout(SETTLE_TIME);
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(SETTLE_TIME);

      // Assert: the matching contact appears based on email search
      const matchingContact = page.getByText(testContactFirstNameEdited, { exact: false });
      await expect(matchingContact.first()).toBeVisible({ timeout: DATA_TIMEOUT });
    });

    test('should clear search and show all results', async () => {
      // Navigate to accounts list with a search active
      await page.goto(ACCOUNTS_URL, {
        waitUntil: 'networkidle',
        timeout: DATA_TIMEOUT,
      });
      await page.waitForTimeout(SETTLE_TIME);

      // Find the search input and enter a filter
      const searchInput = page
        .getByRole('searchbox')
        .or(page.getByPlaceholder(/search/i))
        .or(page.locator('[data-testid="search-input"]'))
        .or(page.locator('[data-testid="filter-input"]'))
        .or(page.getByLabel(/search/i));

      await expect(searchInput.first()).toBeVisible({ timeout: DATA_TIMEOUT });
      await searchInput.first().fill('nonexistent_account_xyz_99999');
      await page.waitForTimeout(SETTLE_TIME);
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(SETTLE_TIME);

      // Assert: no results shown (or an empty state message)
      const noResultsIndicator = page
        .getByText(/no.*(result|record|account|data)/i)
        .or(page.locator('[data-testid="empty-state"]'))
        .or(page.locator('.empty-state'));
      const noResultsVisible = await noResultsIndicator.first().isVisible().catch(() => false);

      // The test account should not be visible with a non-matching search
      const testAccount = page.getByText(testAccountNameEdited, { exact: true });
      const accountVisible = await testAccount.isVisible().catch(() => false);
      // Either we see a no-results message, or the account is hidden
      expect(noResultsVisible || !accountVisible).toBeTruthy();

      // Clear the search to restore full results
      await searchInput.first().clear();
      await page.waitForTimeout(SETTLE_TIME);
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(SETTLE_TIME);

      // Assert: the data table has content again
      const dataTable = page
        .getByRole('table')
        .or(page.locator('[data-testid="data-table"]'))
        .or(page.locator('[data-testid="account-list"]'));
      await expect(dataTable.first()).toBeVisible({ timeout: DATA_TIMEOUT });
    });
  });
});
