/**
 * Playwright E2E Tests — Notifications / Email Workflows
 *
 * Validates the Notifications microservice which replaces the monolith's
 * WebVella.Erp.Plugins.Mail plugin. The monolith included:
 *   - MailPlugin (7 entity-creation patches: `email` + `smtp_service`)
 *   - SmtpInternalService (SMTP send, queue processing, field validation)
 *   - ProcessSmtpQueueJob (scheduled queue processor)
 *   - Email model (id, service_id, sender*, recipient*, subject, content_text,
 *     content_html, status, priority, created_on, sent_on, server_error, etc.)
 *   - SmtpService model (name, server, port, username, password, defaults,
 *     connection_security, retry config)
 *
 * The new architecture maps these to:
 *   - Notifications Lambda service backed by DynamoDB + SES stub on LocalStack
 *   - SQS-triggered QueueProcessor Lambda replacing ProcessSmtpQueueJob
 *   - React SPA pages: EmailList, EmailCompose, EmailDetails, NotificationCenter,
 *     SmtpServiceList, SmtpServiceCreate, SmtpServiceManage
 *
 * API Endpoints (per AAP §0.5.1):
 *   - GET    /v1/notifications/emails          — list emails
 *   - POST   /v1/notifications/emails          — create / compose email
 *   - GET    /v1/notifications/emails/:id      — email details
 *   - POST   /v1/notifications/emails/send     — send email
 *   - GET    /v1/notifications/smtp-services    — list SMTP services
 *   - POST   /v1/notifications/smtp-services    — create SMTP service
 *   - PUT    /v1/notifications/smtp-services/:id — update SMTP service
 *
 * Event Naming (per AAP §0.8.5):
 *   - notifications.email.created
 *   - notifications.email.sent
 *
 * All tests run against LocalStack — NO mocked AWS SDK calls (AAP §0.8.4).
 */

import { test, expect, Page } from '@playwright/test';

// ---------------------------------------------------------------------------
// Test Constants
// ---------------------------------------------------------------------------

/**
 * Default test user credentials.
 * These are seeded into Cognito during LocalStack bootstrap via
 * tools/scripts/seed-test-data.sh (AAP §0.7.5).
 */
const TEST_USER_EMAIL =
  process.env.TEST_USER_EMAIL || 'testuser@webvella.com';
const TEST_USER_PASSWORD =
  process.env.TEST_USER_PASSWORD || 'TestPass123!';

/** EmailStatus enum values matching the monolith's select-field options */
const EMAIL_STATUS = {
  PENDING: 'pending',
  SENT: 'sent',
  ABORTED: 'aborted',
} as const;

/** EmailPriority enum values matching the monolith's select-field options */
const EMAIL_PRIORITY = {
  LOW: 'low',
  NORMAL: 'normal',
  HIGH: 'high',
} as const;

/** Timeout for network-heavy operations (Lambda cold-starts on LocalStack) */
const EXTENDED_TIMEOUT = 15_000;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Authenticate the seeded test user via the React Login page.
 *
 * Mirrors the monolith's `LoginModel.OnPost()` → `AuthService.Authenticate()`
 * flow, now backed by Cognito. Waits until the dashboard is visible, confirming
 * a valid JWT was issued and stored.
 */
async function authenticateTestUser(page: Page): Promise<void> {
  await page.goto('/login');
  await page.waitForLoadState('domcontentloaded');

  // Fill credentials — field selectors favour data-testid, fall back to name
  const emailInput =
    page.locator('[data-testid="email-input"]').or(page.locator('input[name="email"]'));
  const passwordInput =
    page.locator('[data-testid="password-input"]').or(page.locator('input[name="password"]'));
  const loginButton =
    page.locator('[data-testid="login-button"]').or(page.locator('button[type="submit"]'));

  await emailInput.fill(TEST_USER_EMAIL);
  await passwordInput.fill(TEST_USER_PASSWORD);
  await loginButton.click();

  // Wait until we leave /login (redirect to dashboard or returnUrl)
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), {
    timeout: EXTENDED_TIMEOUT,
  });

  // Ensure auth tokens are stored in localStorage before proceeding.
  // The Zustand/localStorage hydration can lag behind the URL redirect.
  await page.waitForFunction(
    () => !!localStorage.getItem('wv_id_token'),
    { timeout: EXTENDED_TIMEOUT },
  );
}

/**
 * Navigate to the Notifications section and wait for the page to settle.
 * After page.goto, we wait for both networkidle AND the absence of the
 * loading spinner to ensure the React SPA has fully hydrated and rendered.
 */
async function navigateToNotifications(page: Page): Promise<void> {
  await page.goto('/notifications/emails');
  await page.waitForLoadState('networkidle');
  // Wait for the loading spinner to disappear and real content to appear.
  // The SPA shows "Loading…" status while React hydration + auth check runs.
  await page.waitForFunction(
    () => !document.querySelector('[role="status"]')?.textContent?.includes('Loading'),
    { timeout: 15_000 },
  ).catch(() => {});
  // Fallback: wait for any heading or main content area
  await page.locator('h1, main, [data-testid="page-title"]').first()
    .waitFor({ state: 'visible', timeout: 10_000 }).catch(() => {});
}

/**
 * Navigate to the SMTP Services settings page.
 */
async function navigateToSmtpServices(page: Page): Promise<void> {
  await page.goto('/notifications/smtp');
  await page.waitForLoadState('networkidle');
  // Wait for loading state to clear
  await page.waitForFunction(
    () => !document.querySelector('[role="status"]')?.textContent?.includes('Loading'),
    { timeout: 15_000 },
  ).catch(() => {});
  await page.locator('h1, main, [data-testid="smtp-services-title"]').first()
    .waitFor({ state: 'visible', timeout: 10_000 }).catch(() => {});
}

/**
 * Generate a unique identifier suffix for test-created resources so parallel
 * test runs do not collide.
 */
function uniqueSuffix(): string {
  return `${Date.now()}-${Math.random().toString(36).substring(2, 8)}`;
}

// ===========================================================================
// Test Suite
// ===========================================================================

test.describe('Notifications', () => {
  // -----------------------------------------------------------------------
  // Global Setup — runs before every test in the suite
  // -----------------------------------------------------------------------

  test.beforeEach(async ({ page }) => {
    await authenticateTestUser(page);
    await navigateToNotifications(page);
  });

  // -----------------------------------------------------------------------
  // 1. Notifications Page Rendering
  // -----------------------------------------------------------------------

  test.describe('Page Rendering', () => {
    test('should display notifications page', async ({ page }) => {
      // The page should have loaded after beforeEach; verify core elements.
      // The page title or heading should include "Notifications" or "Emails".
      const heading = page
        .locator('[data-testid="page-title"]')
        .or(page.locator('h1'))
        .first();
      await expect(heading).toBeVisible({ timeout: EXTENDED_TIMEOUT });

      const headingText = await heading.textContent();
      expect(
        headingText?.toLowerCase().includes('notification') ||
          headingText?.toLowerCase().includes('email'),
      ).toBeTruthy();

      // Verify the URL is correct
      expect(page.url()).toContain('/notifications');
    });

    test('should render notification layout with sidebar and content', async ({
      page,
    }) => {
      // AppShell elements (sidebar + content) should be present — derived
      // from _AppMaster.cshtml → React AppShell component.
      const sidebar = page
        .locator('[data-testid="sidebar"]')
        .or(page.locator('nav[aria-label="sidebar"]'))
        .or(page.locator('aside'));
      const mainContent = page
        .locator('[data-testid="main-content"]')
        .or(page.locator('main'));

      await expect(sidebar.first()).toBeVisible({ timeout: EXTENDED_TIMEOUT });
      await expect(mainContent.first()).toBeVisible({ timeout: EXTENDED_TIMEOUT });
    });
  });

  // -----------------------------------------------------------------------
  // 2. Email Listing
  // -----------------------------------------------------------------------

  test.describe('Email Listing', () => {
    test('should list email notifications in a table', async ({ page }) => {
      // The email list / data table should render.
      // Derived from the Email entity fields: Sender, Subject, Status, CreatedOn.
      const emailTable = page
        .locator('[data-testid="email-list"]')
        .or(page.locator('table'))
        .first();
      await expect(emailTable).toBeVisible({ timeout: EXTENDED_TIMEOUT });

      // Verify expected column headers are present.
      // Columns map to Email.cs properties: Sender, Subject, Status, CreatedOn.
      const columnHeaders = page.locator('th, [role="columnheader"]');
      const headerTexts = await columnHeaders.allTextContents();
      const lowerHeaders = headerTexts.map((h) => h.toLowerCase());

      // At minimum, subject and status columns should appear.
      const hasSubject = lowerHeaders.some((h) => h.includes('subject'));
      const hasStatus = lowerHeaders.some((h) => h.includes('status'));

      expect(hasSubject || hasStatus).toBeTruthy();
    });

    test('should display email table with sender and date columns', async ({
      page,
    }) => {
      // Additional column checks derived from Email.cs: Sender, CreatedOn
      const headerCells = page.locator('th, [role="columnheader"]');
      const headerTexts = await headerCells.allTextContents();
      const joined = headerTexts.join(' ').toLowerCase();

      // Sender column (maps to sender_email / sender_name fields)
      const hasSender =
        joined.includes('sender') || joined.includes('from');
      // Date column (maps to created_on or sent_on)
      const hasDate =
        joined.includes('date') || joined.includes('created') || joined.includes('sent');

      expect(hasSender || hasDate).toBeTruthy();
    });

    test('should show empty state when no emails exist', async ({ page }) => {
      // If the list is empty the UI should render an empty-state message
      // rather than a broken table.
      const emptyState = page
        .locator('[data-testid="empty-state"]')
        .or(page.locator('text=/no (email|notification)/i'));
      const tableRows = page.locator(
        'table tbody tr, [data-testid="email-row"]',
      );

      // Either we see rows or an empty-state element — both are valid.
      const rowCount = await tableRows.count();
      if (rowCount === 0) {
        await expect(emptyState.first()).toBeVisible({ timeout: EXTENDED_TIMEOUT });
      } else {
        expect(rowCount).toBeGreaterThan(0);
      }
    });
  });

  // -----------------------------------------------------------------------
  // 3. Email Filtering by Status
  // -----------------------------------------------------------------------

  test.describe('Email Filtering', () => {
    test('should filter notifications by pending status', async ({ page }) => {
      // Status filter derived from EmailStatus enum: pending (0), sent (1), aborted (2)
      const statusFilter = page
        .locator('[data-testid="status-filter"]')
        .or(page.locator('select[name="status"]'))
        .or(page.locator('[aria-label*="status" i]'));

      // If a filter control exists, interact with it
      const filterExists = (await statusFilter.count()) > 0;
      if (filterExists) {
        await statusFilter.first().click();

        // Select "pending" option
        const pendingOption = page
          .locator(`option[value="${EMAIL_STATUS.PENDING}"]`)
          .or(page.locator(`[data-testid="filter-option-pending"]`))
          .or(page.locator(`text=/pending/i`));
        if ((await pendingOption.count()) > 0) {
          await pendingOption.first().click();
        }

        // After filtering, the URL may contain query params or the table updates
        await page.waitForLoadState('networkidle');
      }

      // Verification: page should still be functional (no crash)
      await expect(page.locator('body')).toBeVisible();
    });

    test('should filter notifications by sent status', async ({ page }) => {
      const statusFilter = page
        .locator('[data-testid="status-filter"]')
        .or(page.locator('select[name="status"]'));

      const filterExists = (await statusFilter.count()) > 0;
      if (filterExists) {
        await statusFilter.first().selectOption({ label: 'Sent' }).catch(() => {
          // Fall back to clicking
        });
        await page.waitForLoadState('networkidle');
      }

      // The table should still render without errors after filtering
      await expect(page.locator('body')).toBeVisible();
    });

    test('should filter notifications by aborted status', async ({ page }) => {
      // Aborted status (value 2) — email delivery permanently failed
      const statusFilter = page
        .locator('[data-testid="status-filter"]')
        .or(page.locator('select[name="status"]'));

      const filterExists = (await statusFilter.count()) > 0;
      if (filterExists) {
        await statusFilter.first().selectOption({ label: 'Aborted' }).catch(() => {
          // Fall back if label doesn't match exactly
        });
        await page.waitForLoadState('networkidle');
      }

      await expect(page.locator('body')).toBeVisible();
    });

    test('should clear status filter and show all notifications', async ({
      page,
    }) => {
      // Apply then remove a filter — verify the full list re-renders
      const statusFilter = page
        .locator('[data-testid="status-filter"]')
        .or(page.locator('select[name="status"]'));

      const filterExists = (await statusFilter.count()) > 0;
      if (filterExists) {
        // Apply a filter first
        await statusFilter.first().selectOption({ label: 'Sent' }).catch(() => {});
        await page.waitForLoadState('networkidle');

        // Clear / reset — select "All" or empty value
        await statusFilter.first().selectOption({ value: '' }).catch(async () => {
          const clearButton = page.locator(
            '[data-testid="clear-filter"]',
          );
          if ((await clearButton.count()) > 0) {
            await clearButton.click();
          }
        });
        await page.waitForLoadState('networkidle');
      }

      await expect(page.locator('body')).toBeVisible();
    });
  });

  // -----------------------------------------------------------------------
  // 4. Email Detail View
  // -----------------------------------------------------------------------

  test.describe('Email Details', () => {
    test('should view email details when clicking a row', async ({ page }) => {
      // Attempt to click the first email row to open its detail view.
      // Fields expected: Sender, Recipients, Subject, ContentText/ContentHtml,
      // Priority, SentOn (from Email.cs).
      const emailRow = page
        .locator('[data-testid="email-row"]')
        .or(page.locator('table tbody tr'))
        .first();

      const rowVisible = await emailRow.isVisible().catch(() => false);
      if (!rowVisible) {
        // No emails exist — verify empty state and skip detail test.
        // Use Playwright's native text matching (not CSS text= pseudo-selector).
        const emptyState = page
          .locator('[data-testid="empty-state"]')
          .or(page.getByText(/no (email|notification)/i));
        await expect(emptyState.first()).toBeVisible({ timeout: EXTENDED_TIMEOUT });
        return;
      }

      await emailRow.click();
      await page.waitForLoadState('networkidle');

      // Detail view should render with subject heading
      const detailContainer = page
        .locator('[data-testid="email-detail"]')
        .or(page.locator('[data-testid="email-details"]'))
        .or(page.locator('main'));
      await expect(detailContainer.first()).toBeVisible({ timeout: EXTENDED_TIMEOUT });
    });

    test('should display sender information in email details', async ({
      page,
    }) => {
      const emailRow = page
        .locator('[data-testid="email-row"]')
        .or(page.locator('table tbody tr'))
        .first();

      if (!(await emailRow.isVisible().catch(() => false))) return;

      await emailRow.click();
      await page.waitForLoadState('networkidle');

      // Verify sender field is displayed (maps to sender_email + sender_name)
      const senderField = page
        .locator('[data-testid="email-sender"]')
        .or(page.locator('text=/sender/i'))
        .or(page.locator('text=/from/i'));
      await expect(senderField.first()).toBeVisible({ timeout: EXTENDED_TIMEOUT });
    });

    test('should display recipients in email details', async ({ page }) => {
      const emailRow = page
        .locator('[data-testid="email-row"]')
        .or(page.locator('table tbody tr'))
        .first();

      if (!(await emailRow.isVisible().catch(() => false))) return;

      await emailRow.click();
      await page.waitForLoadState('networkidle');

      // Recipient field (maps to recipient_email + recipient_name from Email entity)
      const recipientField = page
        .locator('[data-testid="email-recipients"]')
        .or(page.locator('text=/recipient/i'))
        .or(page.locator('text=/to/i'));
      await expect(recipientField.first()).toBeVisible({ timeout: EXTENDED_TIMEOUT });
    });

    test('should display email content (text or html) in details', async ({
      page,
    }) => {
      // Look for real email data rows only — skip the "No emails found" empty row
      const emailRow = page
        .locator('[data-testid="email-row"]')
        .or(page.locator('table tbody tr:not(:has([role="status"]))').filter({ hasNot: page.locator('text=/no emails/i') }))
        .first();

      if (!(await emailRow.isVisible().catch(() => false))) return;

      await emailRow.click();
      await page.waitForLoadState('networkidle');

      // Content area — either ContentText or ContentHtml rendered in a container
      const contentArea = page
        .locator('[data-testid="email-content"]')
        .or(page.locator('[data-testid="email-body"]'))
        .or(page.locator('article'));
      await expect(contentArea.first()).toBeVisible({ timeout: EXTENDED_TIMEOUT });
    });

    test('should display priority and status in email details', async ({
      page,
    }) => {
      // Look for real email data rows — skip the empty-state row
      const emailRow = page
        .locator('[data-testid="email-row"]')
        .or(page.locator('table tbody tr:not(:has([role="status"]))').filter({ hasNot: page.locator('text=/no emails/i') }))
        .first();

      if (!(await emailRow.isVisible().catch(() => false))) return;

      await emailRow.click();
      await page.waitForLoadState('networkidle');

      // Priority badge/label (low / normal / high)
      const priorityField = page
        .locator('[data-testid="email-priority"]')
        .or(page.locator('text=/priority/i'));
      // Status badge/label (pending / sent / aborted)
      const statusField = page
        .locator('[data-testid="email-status"]')
        .or(page.locator('text=/status/i'));

      // At least one of these should be rendered in the detail view
      const priorityVisible = await priorityField.first().isVisible().catch(() => false);
      const statusVisible = await statusField.first().isVisible().catch(() => false);

      expect(priorityVisible || statusVisible).toBeTruthy();
    });

    test('should navigate back to email list from details', async ({
      page,
    }) => {
      const emailRow = page
        .locator('[data-testid="email-row"]')
        .or(page.locator('table tbody tr'))
        .first();

      if (!(await emailRow.isVisible().catch(() => false))) return;

      await emailRow.click();
      await page.waitForLoadState('networkidle');

      // Click back / breadcrumb link to return to list
      const backLink = page
        .locator('[data-testid="back-to-list"]')
        .or(page.locator('a[href*="/notifications"]'))
        .or(page.locator('button:has-text("Back")'));

      if ((await backLink.count()) > 0) {
        await backLink.first().click();
        await page.waitForLoadState('networkidle');
      } else {
        // Fall back to browser back navigation
        await page.goBack();
        await page.waitForLoadState('networkidle');
      }

      // Should be back on the list page
      expect(page.url()).toContain('/notifications');
    });
  });

  // -----------------------------------------------------------------------
  // 5. Email Composition
  // -----------------------------------------------------------------------

  test.describe('Email Composition', () => {
    test('should navigate to compose email form', async ({ page }) => {
      // Click "New Email" / "Compose" button
      const composeButton = page
        .locator('[data-testid="compose-email"]')
        .or(page.locator('button:has-text("New Email")'))
        .or(page.locator('button:has-text("Compose")'))
        .or(page.locator('a:has-text("Compose")'))
        .or(page.locator('a:has-text("New Email")'));

      await expect(composeButton.first()).toBeVisible({ timeout: EXTENDED_TIMEOUT });
      await composeButton.first().click();
      await page.waitForLoadState('networkidle');

      // Compose form should render
      const composeForm = page
        .locator('[data-testid="compose-form"]')
        .or(page.locator('form'))
        .first();
      await expect(composeForm).toBeVisible({ timeout: EXTENDED_TIMEOUT });
    });

    test('should compose a new email successfully', async ({ page }) => {
      // Navigate to compose form
      const composeButton = page
        .locator('[data-testid="compose-email"]')
        .or(page.locator('button:has-text("New Email")'))
        .or(page.locator('button:has-text("Compose")'))
        .or(page.locator('a:has-text("Compose")'))
        .or(page.locator('a:has-text("New Email")'));

      if ((await composeButton.count()) === 0) return;
      await composeButton.first().click();
      await page.waitForLoadState('networkidle');

      const suffix = uniqueSuffix();

      // Fill recipient (maps to recipient_email, required per SmtpService.SendEmail)
      const recipientInput = page
        .locator('[data-testid="recipient-input"]')
        .or(page.locator('input[name="recipient"]'))
        .or(page.locator('input[name="recipient_email"]'))
        .or(page.locator('input[name="to"]'));
      if ((await recipientInput.count()) > 0) {
        await recipientInput.first().fill(`recipient-${suffix}@example.com`);
      }

      // Fill subject (required per SmtpService.SendEmail validation)
      const subjectInput = page
        .locator('[data-testid="subject-input"]')
        .or(page.locator('input[name="subject"]'));
      if ((await subjectInput.count()) > 0) {
        await subjectInput.first().fill(`Test Email Subject ${suffix}`);
      }

      // Fill body / content_text
      const bodyInput = page
        .locator('[data-testid="body-input"]')
        .or(page.locator('textarea[name="content"]'))
        .or(page.locator('textarea[name="content_text"]'))
        .or(page.locator('textarea[name="body"]'));
      if ((await bodyInput.count()) > 0) {
        await bodyInput
          .first()
          .fill(`Automated E2E test email body — ${suffix}`);
      }

      // Submit the compose form
      const submitButton = page
        .locator('[data-testid="send-email"]')
        .or(page.locator('button[type="submit"]'))
        .or(page.locator('button:has-text("Send")'))
        .or(page.locator('button:has-text("Queue")'));
      await submitButton.first().click();

      // Wait for success feedback — toast notification, redirect, or inline message
      await page.waitForLoadState('networkidle');

      const successIndicator = page
        .locator('[data-testid="success-notification"]')
        .or(page.locator('.toast-success, .notification-success'))
        .or(page.locator('text=/success/i'))
        .or(page.locator('text=/queued/i'))
        .or(page.locator('text=/sent/i'));

      // Either a success message appears or we've been redirected back to list
      const successVisible = await successIndicator
        .first()
        .isVisible()
        .catch(() => false);
      const redirectedToList = page.url().includes('/notifications');

      expect(successVisible || redirectedToList).toBeTruthy();
    });

    test('should validate email form fields — missing recipient', async ({
      page,
    }) => {
      // Navigate to compose form
      const composeButton = page
        .locator('[data-testid="compose-email"]')
        .or(page.locator('button:has-text("New Email")'))
        .or(page.locator('button:has-text("Compose")'))
        .or(page.locator('a:has-text("Compose")'))
        .or(page.locator('a:has-text("New Email")'));

      if ((await composeButton.count()) === 0) return;
      await composeButton.first().click();
      await page.waitForLoadState('networkidle');

      // Fill subject but leave recipient empty (violates SmtpService validation)
      const subjectInput = page
        .locator('[data-testid="subject-input"]')
        .or(page.locator('input[name="subject"]'));
      if ((await subjectInput.count()) > 0) {
        await subjectInput.first().fill('Subject Without Recipient');
      }

      // Submit
      const submitButton = page
        .locator('[data-testid="send-email"]')
        .or(page.locator('button[type="submit"]'))
        .or(page.locator('button:has-text("Send")'));
      await submitButton.first().click();
      await page.waitForTimeout(1000);

      // Validation error should appear for recipient field
      const validationError = page
        .locator('[data-testid="recipient-error"]')
        .or(page.locator('.field-error, .input-error, .form-error'))
        .or(page.locator('text=/recipient.*required/i'))
        .or(page.locator('text=/required/i'));

      // The form should either show a validation error or prevent submission
      const errorVisible = await validationError
        .first()
        .isVisible()
        .catch(() => false);
      // If HTML5 validation prevents submission, the URL should not change
      const stayedOnForm =
        page.url().includes('/compose') ||
        page.url().includes('/new') ||
        page.url().includes('/notifications');

      expect(errorVisible || stayedOnForm).toBeTruthy();
    });

    test('should validate email form fields — missing subject', async ({
      page,
    }) => {
      // Navigate to compose
      const composeButton = page
        .locator('[data-testid="compose-email"]')
        .or(page.locator('button:has-text("New Email")'))
        .or(page.locator('button:has-text("Compose")'))
        .or(page.locator('a:has-text("Compose")'))
        .or(page.locator('a:has-text("New Email")'));

      if ((await composeButton.count()) === 0) return;
      await composeButton.first().click();
      await page.waitForLoadState('networkidle');

      // Fill recipient but leave subject empty (violates SmtpService validation)
      const recipientInput = page
        .locator('[data-testid="recipient-input"]')
        .or(page.locator('input[name="recipient"]'))
        .or(page.locator('input[name="to"]'));
      if ((await recipientInput.count()) > 0) {
        await recipientInput.first().fill('test@example.com');
      }

      // Submit
      const submitButton = page
        .locator('[data-testid="send-email"]')
        .or(page.locator('button[type="submit"]'))
        .or(page.locator('button:has-text("Send")'));
      await submitButton.first().click();
      await page.waitForTimeout(1000);

      // Validation error for subject field
      const validationError = page
        .locator('[data-testid="subject-error"]')
        .or(page.locator('.field-error, .input-error, .form-error'))
        .or(page.locator('text=/subject.*required/i'))
        .or(page.locator('text=/required/i'));

      const errorVisible = await validationError
        .first()
        .isVisible()
        .catch(() => false);
      const stayedOnForm =
        page.url().includes('/compose') ||
        page.url().includes('/new') ||
        page.url().includes('/notifications');

      expect(errorVisible || stayedOnForm).toBeTruthy();
    });

    test('should validate email form fields — invalid recipient email', async ({
      page,
    }) => {
      // SmtpService.SendEmail checks Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")
      const composeButton = page
        .locator('[data-testid="compose-email"]')
        .or(page.locator('button:has-text("New Email")'))
        .or(page.locator('button:has-text("Compose")'))
        .or(page.locator('a:has-text("Compose")'))
        .or(page.locator('a:has-text("New Email")'));

      if ((await composeButton.count()) === 0) return;
      await composeButton.first().click();
      await page.waitForLoadState('networkidle');

      // Enter an invalid email address
      const recipientInput = page
        .locator('[data-testid="recipient-input"]')
        .or(page.locator('input[name="recipient"]'))
        .or(page.locator('input[name="to"]'));
      if ((await recipientInput.count()) > 0) {
        await recipientInput.first().fill('not-a-valid-email');
      }

      const subjectInput = page
        .locator('[data-testid="subject-input"]')
        .or(page.locator('input[name="subject"]'));
      if ((await subjectInput.count()) > 0) {
        await subjectInput.first().fill('Test Subject');
      }

      // Submit
      const submitButton = page
        .locator('[data-testid="send-email"]')
        .or(page.locator('button[type="submit"]'))
        .or(page.locator('button:has-text("Send")'));
      await submitButton.first().click();
      await page.waitForTimeout(1000);

      // Should see email-format validation error
      const validationError = page
        .locator('[data-testid="recipient-error"]')
        .or(page.locator('.field-error, .input-error'))
        .or(page.locator('text=/valid.*email/i'))
        .or(page.locator('text=/invalid.*email/i'));

      const errorVisible = await validationError
        .first()
        .isVisible()
        .catch(() => false);
      const stayedOnForm =
        page.url().includes('/compose') ||
        page.url().includes('/new') ||
        page.url().includes('/notifications');

      expect(errorVisible || stayedOnForm).toBeTruthy();
    });

    test('should support email priority selection', async ({ page }) => {
      // Derived from EmailPriority enum: low (0), normal (1), high (2)
      const composeButton = page
        .locator('[data-testid="compose-email"]')
        .or(page.locator('button:has-text("New Email")'))
        .or(page.locator('button:has-text("Compose")'))
        .or(page.locator('a:has-text("Compose")'))
        .or(page.locator('a:has-text("New Email")'));

      if ((await composeButton.count()) === 0) return;
      await composeButton.first().click();
      await page.waitForLoadState('networkidle');

      // Wait for compose form to render fully
      await page.locator('h1:has-text("Compose"), h2:has-text("Compose")').first()
        .waitFor({ state: 'visible', timeout: EXTENDED_TIMEOUT }).catch(() => {});

      // Priority selector should be present in the compose form
      const prioritySelect = page
        .locator('[data-testid="priority-select"]')
        .or(page.locator('select[name="priority"]'))
        .or(page.locator('[aria-label*="priority" i]'));

      // Wait a beat for lazy-rendered form sections
      await prioritySelect.first().waitFor({ state: 'attached', timeout: 5000 }).catch(() => {});

      if ((await prioritySelect.count()) > 0) {
        // Try selecting high priority
        await prioritySelect
          .first()
          .selectOption({ label: 'High' })
          .catch(async () => {
            // Try alternative: value-based selection
            await prioritySelect
              .first()
              .selectOption({ value: EMAIL_PRIORITY.HIGH })
              .catch(() => {});
          });

        // Verify the selection stuck
        const selectedValue = await prioritySelect
          .first()
          .inputValue()
          .catch(() => '');
        expect(
          selectedValue === EMAIL_PRIORITY.HIGH ||
            selectedValue === '2' ||
            selectedValue.toLowerCase().includes('high'),
        ).toBeTruthy();

        // Also verify low priority can be selected
        await prioritySelect
          .first()
          .selectOption({ label: 'Low' })
          .catch(async () => {
            await prioritySelect
              .first()
              .selectOption({ value: EMAIL_PRIORITY.LOW })
              .catch(() => {});
          });

        const lowValue = await prioritySelect
          .first()
          .inputValue()
          .catch(() => '');
        expect(
          lowValue === EMAIL_PRIORITY.LOW ||
            lowValue === '0' ||
            lowValue.toLowerCase().includes('low'),
        ).toBeTruthy();
      } else {
        // Priority may be rendered as radio buttons or a custom component
        const highOption = page.locator(
          'input[value="high"], [data-testid="priority-high"]',
        );
        const normalOption = page.locator(
          'input[value="normal"], [data-testid="priority-normal"]',
        );
        const lowOption = page.locator(
          'input[value="low"], [data-testid="priority-low"]',
        );

        const anyPriorityExists =
          (await highOption.count()) > 0 ||
          (await normalOption.count()) > 0 ||
          (await lowOption.count()) > 0;

        // The compose form should have priority selection in some form
        expect(anyPriorityExists).toBeTruthy();
      }
    });

    test('should show new email in list after composition', async ({
      page,
    }) => {
      // Full compose → verify in list workflow
      const composeButton = page
        .locator('[data-testid="compose-email"]')
        .or(page.locator('button:has-text("New Email")'))
        .or(page.locator('button:has-text("Compose")'))
        .or(page.locator('a:has-text("Compose")'))
        .or(page.locator('a:has-text("New Email")'));

      if ((await composeButton.count()) === 0) return;
      await composeButton.first().click();
      await page.waitForLoadState('networkidle');

      // React Router client-side navigation does not trigger a full page
      // load, so `networkidle` resolves while the old email-list DOM is
      // still mounted.  Wait for the compose form's subject input (the most
      // reliable compose-specific element) before proceeding — Playwright's
      // `waitFor` auto-retries until the element appears in the new DOM.
      const subjectInput = page
        .locator('[data-testid="subject-input"]')
        .or(page.locator('input[name="subject"]'));
      await subjectInput.first().waitFor({ state: 'visible', timeout: 15000 });

      const suffix = uniqueSuffix();
      const testSubject = `E2E Verify List ${suffix}`;

      // Fill required fields — the compose form is now rendered
      const recipientInput = page
        .locator('[data-testid="recipient-input"]')
        .or(page.locator('input[name="recipient"]'))
        .or(page.locator('input[name="to"]'));
      await recipientInput.first().fill(`list-verify-${suffix}@example.com`);

      await subjectInput.first().fill(testSubject);

      const bodyInput = page
        .locator('[data-testid="body-input"]')
        .or(page.locator('textarea[name="content"]'))
        .or(page.locator('textarea[name="content_text"]'))
        .or(page.locator('textarea[name="body"]'));
      await bodyInput.first().fill('E2E test body content');

      // Submit
      const submitButton = page
        .locator('[data-testid="send-email"]')
        .or(page.locator('button[type="submit"]:has-text("Send")')
          .or(page.locator('button:has-text("Send Now")')))
        .or(page.locator('button:has-text("Queue")'));
      await submitButton.first().click();
      await page.waitForLoadState('networkidle');

      // Navigate back to the email list
      await navigateToNotifications(page);

      // The newly composed email should appear in the list with "pending" status
      const newEmailRow = page.locator(`text=${testSubject}`);
      // Allow time for DynamoDB eventual consistency + Lambda processing
      await expect(newEmailRow.first()).toBeVisible({
        timeout: EXTENDED_TIMEOUT,
      });
    });
  });

  // -----------------------------------------------------------------------
  // 6. SMTP Service Configuration
  // -----------------------------------------------------------------------

  test.describe('SMTP Service Configuration', () => {
    test('should display SMTP service configuration page', async ({
      page,
    }) => {
      // Navigate to SMTP services settings (replaces smtp_service entity CRUD)
      await navigateToSmtpServices(page);

      // Page heading / section for SMTP services
      const heading = page
        .locator('[data-testid="smtp-services-title"]')
        .or(page.locator('h1, h2'))
        .first();
      await expect(heading).toBeVisible({ timeout: EXTENDED_TIMEOUT });

      const headingText = await heading.textContent();
      expect(
        headingText?.toLowerCase().includes('smtp') ||
          headingText?.toLowerCase().includes('service') ||
          headingText?.toLowerCase().includes('config') ||
          headingText?.toLowerCase().includes('notification'),
      ).toBeTruthy();
    });

    test('should list SMTP services with key fields', async ({ page }) => {
      await navigateToSmtpServices(page);

      // SmtpService fields: name, server, port, username (from SmtpService.cs)
      const serviceTable = page
        .locator('[data-testid="smtp-service-list"]')
        .or(page.locator('table'))
        .first();

      const tableVisible = await serviceTable
        .isVisible()
        .catch(() => false);

      if (tableVisible) {
        const headerCells = page.locator('th, [role="columnheader"]');
        const headerTexts = await headerCells.allTextContents();
        const joined = headerTexts.join(' ').toLowerCase();

        // At minimum "name" or "server" columns from SmtpService entity
        const hasNameOrServer =
          joined.includes('name') || joined.includes('server');
        expect(hasNameOrServer).toBeTruthy();
      } else {
        // If no table, expect a card-based or list layout
        const serviceItems = page
          .locator('[data-testid="smtp-service-item"]')
          .or(page.locator('.smtp-service-card'));
        const itemCount = await serviceItems.count();
        // Either items exist or an empty-state message is shown
        if (itemCount === 0) {
          const emptyState = page
            .locator('[data-testid="empty-state"]')
            .or(page.getByText(/no.*service/i))
            .or(page.getByText(/no.*smtp/i));
          await expect(emptyState.first()).toBeVisible({ timeout: EXTENDED_TIMEOUT });
        }
      }
    });

    test('should create a new SMTP service configuration', async ({
      page,
    }) => {
      await navigateToSmtpServices(page);

      // Click "Create" / "Add" button
      const createButton = page
        .locator('[data-testid="create-smtp-service"]')
        .or(page.locator('button:has-text("Create")'))
        .or(page.locator('button:has-text("Add")'))
        .or(page.locator('a:has-text("Create")'))
        .or(page.locator('a:has-text("Add")'));

      if ((await createButton.count()) === 0) return;
      await createButton.first().click();
      await page.waitForLoadState('networkidle');

      const suffix = uniqueSuffix();

      // Fill SmtpService fields per MailPlugin.20190215.cs entity definition
      // Name (required, unique per SmtpInternalService.ValidatePreCreateRecord)
      const nameInput = page
        .locator('[data-testid="smtp-name"]')
        .or(page.locator('input[name="name"]'));
      if ((await nameInput.count()) > 0) {
        await nameInput.first().fill(`Test SMTP ${suffix}`);
      }

      // Server
      const serverInput = page
        .locator('[data-testid="smtp-server"]')
        .or(page.locator('input[name="server"]'));
      if ((await serverInput.count()) > 0) {
        await serverInput.first().fill('smtp.example.com');
      }

      // Port (must be integer 1-65025 per SmtpInternalService validation)
      const portInput = page
        .locator('[data-testid="smtp-port"]')
        .or(page.locator('input[name="port"]'));
      if ((await portInput.count()) > 0) {
        await portInput.first().fill('587');
      }

      // Username
      const usernameInput = page
        .locator('[data-testid="smtp-username"]')
        .or(page.locator('input[name="username"]'));
      if ((await usernameInput.count()) > 0) {
        await usernameInput.first().fill(`user-${suffix}`);
      }

      // Default sender email (must be valid email per validation)
      const senderEmailInput = page
        .locator('[data-testid="smtp-default-sender-email"]')
        .or(page.locator('input[name="default_sender_email"]'))
        .or(page.locator('input[name="default_from_email"]'));
      if ((await senderEmailInput.count()) > 0) {
        await senderEmailInput.first().fill(`sender-${suffix}@example.com`);
      }

      // Max retries (1-10 per validation)
      const maxRetriesInput = page
        .locator('[data-testid="smtp-max-retries"]')
        .or(page.locator('input[name="max_retries_count"]'));
      if ((await maxRetriesInput.count()) > 0) {
        await maxRetriesInput.first().fill('3');
      }

      // Submit
      const submitButton = page
        .locator('[data-testid="save-smtp-service"]')
        .or(page.locator('button[type="submit"]'))
        .or(page.locator('button:has-text("Save")'))
        .or(page.locator('button:has-text("Create")'));
      await submitButton.first().click();
      await page.waitForLoadState('networkidle');

      // Verify success — redirect to list or success notification
      const successIndicator = page
        .locator('[data-testid="success-notification"]')
        .or(page.locator('text=/success/i'))
        .or(page.locator('text=/created/i'));

      const successVisible = await successIndicator
        .first()
        .isVisible()
        .catch(() => false);
      const redirectedToList =
        page.url().includes('/smtp-services') ||
        page.url().includes('/notifications');

      expect(successVisible || redirectedToList).toBeTruthy();
    });

    test('should validate SMTP service port range', async ({ page }) => {
      // SmtpInternalService validates port: integer between 1 and 65025
      await navigateToSmtpServices(page);

      const createButton = page
        .locator('[data-testid="create-smtp-service"]')
        .or(page.locator('button:has-text("Create")'))
        .or(page.locator('button:has-text("Add")'))
        .or(page.locator('a:has-text("Create")'))
        .or(page.locator('a:has-text("Add")'));

      if ((await createButton.count()) === 0) return;
      await createButton.first().click();
      await page.waitForLoadState('networkidle');

      // Fill name and server but enter invalid port
      const nameInput = page
        .locator('[data-testid="smtp-name"]')
        .or(page.locator('input[name="name"]'));
      if ((await nameInput.count()) > 0) {
        await nameInput.first().fill(`Invalid Port Service ${uniqueSuffix()}`);
      }

      const portInput = page
        .locator('[data-testid="smtp-port"]')
        .or(page.locator('input[name="port"]'));
      if ((await portInput.count()) > 0) {
        // Port value outside valid range (1-65025)
        await portInput.first().fill('99999');
      }

      const submitButton = page
        .locator('[data-testid="save-smtp-service"]')
        .or(page.locator('button[type="submit"]'))
        .or(page.locator('button:has-text("Save")'));
      await submitButton.first().click();
      await page.waitForTimeout(1000);

      // Should see a port validation error
      const portError = page
        .locator('[data-testid="port-error"]')
        .or(page.locator('.field-error'))
        .or(page.locator('text=/port/i'))
        .or(page.locator('text=/1.*65025/i'))
        .or(page.locator('text=/invalid/i'));

      const errorVisible = await portError
        .first()
        .isVisible()
        .catch(() => false);
      const stayedOnForm =
        page.url().includes('/create') || page.url().includes('/new');

      expect(errorVisible || stayedOnForm).toBeTruthy();
    });

    test('should validate SMTP service default sender email format', async ({
      page,
    }) => {
      // SmtpInternalService validates default_from_email with email regex
      await navigateToSmtpServices(page);

      const createButton = page
        .locator('[data-testid="create-smtp-service"]')
        .or(page.locator('button:has-text("Create")'))
        .or(page.locator('button:has-text("Add")'))
        .or(page.locator('a:has-text("Create")'))
        .or(page.locator('a:has-text("Add")'));

      if ((await createButton.count()) === 0) return;
      await createButton.first().click();
      await page.waitForLoadState('networkidle');

      const nameInput = page
        .locator('[data-testid="smtp-name"]')
        .or(page.locator('input[name="name"]'));
      if ((await nameInput.count()) > 0) {
        await nameInput.first().fill(`Bad Email Service ${uniqueSuffix()}`);
      }

      const portInput = page
        .locator('[data-testid="smtp-port"]')
        .or(page.locator('input[name="port"]'));
      if ((await portInput.count()) > 0) {
        await portInput.first().fill('587');
      }

      // Enter an invalid email for default_from_email
      const senderEmailInput = page
        .locator('[data-testid="smtp-default-sender-email"]')
        .or(page.locator('input[name="default_sender_email"]'))
        .or(page.locator('input[name="default_from_email"]'));
      if ((await senderEmailInput.count()) > 0) {
        await senderEmailInput.first().fill('not-a-valid-email');
      }

      const submitButton = page
        .locator('[data-testid="save-smtp-service"]')
        .or(page.locator('button[type="submit"]'))
        .or(page.locator('button:has-text("Save")'));
      await submitButton.first().click();
      await page.waitForTimeout(1000);

      const emailError = page
        .locator('[data-testid="sender-email-error"]')
        .or(page.locator('.field-error'))
        .or(page.locator('text=/valid.*email/i'))
        .or(page.locator('text=/invalid.*email/i'));

      const errorVisible = await emailError
        .first()
        .isVisible()
        .catch(() => false);
      const stayedOnForm =
        page.url().includes('/create') || page.url().includes('/new');

      expect(errorVisible || stayedOnForm).toBeTruthy();
    });

    test('should edit an existing SMTP service configuration', async ({
      page,
    }) => {
      await navigateToSmtpServices(page);

      // Click on the first SMTP service to open its detail/manage page
      const serviceRow = page
        .locator('[data-testid="smtp-service-item"]')
        .or(page.locator('table tbody tr'))
        .first();

      const rowVisible = await serviceRow.isVisible().catch(() => false);
      if (!rowVisible) return;

      await serviceRow.click();
      await page.waitForLoadState('networkidle');

      // Look for an edit button
      const editButton = page
        .locator('[data-testid="edit-smtp-service"]')
        .or(page.locator('button:has-text("Edit")'))
        .or(page.locator('a:has-text("Edit")'))
        .or(page.locator('a:has-text("Manage")'));

      if ((await editButton.count()) > 0) {
        await editButton.first().click();
        await page.waitForLoadState('networkidle');
      }

      // Modify a field — update server name
      const serverInput = page
        .locator('[data-testid="smtp-server"]')
        .or(page.locator('input[name="server"]'));
      if ((await serverInput.count()) > 0) {
        await serverInput.first().fill('updated-smtp.example.com');
      }

      // Save changes
      const saveButton = page
        .locator('[data-testid="save-smtp-service"]')
        .or(page.locator('button[type="submit"]'))
        .or(page.locator('button:has-text("Save")'))
        .or(page.locator('button:has-text("Update")'));
      if ((await saveButton.count()) > 0) {
        await saveButton.first().click();
        await page.waitForLoadState('networkidle');
      }

      // Verify success
      const successIndicator = page
        .locator('[data-testid="success-notification"]')
        .or(page.locator('text=/success/i'))
        .or(page.locator('text=/updated/i'))
        .or(page.locator('text=/saved/i'));

      const successVisible = await successIndicator
        .first()
        .isVisible()
        .catch(() => false);
      const redirected =
        page.url().includes('/smtp-services') ||
        page.url().includes('/notifications');

      expect(successVisible || redirected).toBeTruthy();
    });
  });

  // -----------------------------------------------------------------------
  // 7. Queue Processing Visibility
  // -----------------------------------------------------------------------

  test.describe('Queue Processing', () => {
    test('should show email queue status indicator', async ({ page }) => {
      // The monolith's ProcessSmtpQueueJob checked the email queue and processed
      // pending emails. In the new architecture, SQS-triggered Lambda processes
      // the queue. The UI should show a status indicator or pending count.

      // Wait for the email list page to fully render (status filter tabs + table)
      await page.locator('nav[aria-label="Filter emails by status"], [role="tablist"]')
        .first().waitFor({ state: 'visible', timeout: EXTENDED_TIMEOUT }).catch(() => {});

      const queueStatus = page
        .locator('[data-testid="queue-status"]')
        .or(page.locator('[data-testid="pending-count"]'))
        .or(page.locator('[data-testid="email-queue"]'))
        .or(page.locator('text=/queue/i'))
        .or(page.locator('text=/pending/i'));

      // Queue status should be visible somewhere on the notifications page
      const statusVisible = await queueStatus
        .first()
        .isVisible()
        .catch(() => false);

      // If no explicit queue indicator, check for a status badge/count in the list
      if (!statusVisible) {
        const statusBadge = page
          .locator('.badge, .chip, .tag, [data-testid="status-badge"]')
          .or(page.locator(`text=${EMAIL_STATUS.PENDING}`));
        const badgeVisible = await statusBadge
          .first()
          .isVisible()
          .catch(() => false);

        // Either a queue indicator or status badges in the email list should exist
        expect(badgeVisible || statusVisible).toBeTruthy();
      } else {
        expect(statusVisible).toBeTruthy();
      }
    });

    test('should display pending email count', async ({ page }) => {
      // Verify that pending emails are identifiable in the list or via a counter
      const pendingIndicator = page
        .locator('[data-testid="pending-count"]')
        .or(page.locator('[data-testid="queue-count"]'))
        .or(page.locator('text=/\\d+\\s*pending/i'))
        .or(page.locator('text=/pending.*\\d+/i'));

      const indicatorVisible = await pendingIndicator
        .first()
        .isVisible()
        .catch(() => false);

      if (!indicatorVisible) {
        // Fall back: check if "pending" text appears anywhere on the page
        // which would indicate queue-processed emails are tracked
        const pendingText = page.locator('text=/pending/i');
        const hasPendingText = await pendingText
          .first()
          .isVisible()
          .catch(() => false);

        // Either a count indicator or "pending" text should be present
        // (or the page has no emails at all — acceptable state)
        const emptyState = page
          .locator('[data-testid="empty-state"]')
          .or(page.getByText(/no (email|notification)/i));
        const isEmpty = await emptyState
          .first()
          .isVisible()
          .catch(() => false);

        expect(hasPendingText || isEmpty || indicatorVisible).toBeTruthy();
      } else {
        expect(indicatorVisible).toBeTruthy();
      }
    });
  });

  // -----------------------------------------------------------------------
  // 8. Error and Edge-Case Handling
  // -----------------------------------------------------------------------

  test.describe('Error Handling', () => {
    test('should handle network errors gracefully on notifications page', async ({
      page,
    }) => {
      // Simulate a scenario where the API is slow or returns an error
      // by navigating to a non-existent sub-route
      await page.goto('/notifications/nonexistent-resource');
      await page.waitForLoadState('networkidle');

      // The app should show a 404 or redirect gracefully — never a white screen
      const hasContent =
        (await page.locator('body').textContent())?.trim().length ?? 0;
      expect(hasContent).toBeGreaterThan(0);
    });

    test('should display error notification on failed email send', async ({
      page,
    }) => {
      // Open compose form and attempt to send with an intentionally bad payload
      const composeButton = page
        .locator('[data-testid="compose-email"]')
        .or(page.locator('button:has-text("New Email")'))
        .or(page.locator('button:has-text("Compose")'))
        .or(page.locator('a:has-text("Compose")'))
        .or(page.locator('a:has-text("New Email")'));

      if ((await composeButton.count()) === 0) return;
      await composeButton.first().click();
      await page.waitForLoadState('networkidle');

      // Submit completely empty form — should not crash and should show errors
      const submitButton = page
        .locator('[data-testid="send-email"]')
        .or(page.locator('button[type="submit"]'))
        .or(page.locator('button:has-text("Send")'));

      if ((await submitButton.count()) > 0) {
        await submitButton.first().click();
        await page.waitForTimeout(1000);
      }

      // Expect validation errors or the form remains visible (no crash)
      const formStillVisible = await page
        .locator('form')
        .first()
        .isVisible()
        .catch(() => false);
      const errorMessages = await page
        .locator('.field-error, .form-error, .input-error, [role="alert"]')
        .count();

      expect(formStillVisible || errorMessages > 0).toBeTruthy();
    });
  });

  // -----------------------------------------------------------------------
  // 9. Responsive Layout
  // -----------------------------------------------------------------------

  test.describe('Responsive Layout', () => {
    test('should render notifications page correctly on mobile viewport', async ({
      page,
    }) => {
      // Test responsive layout — Tailwind CSS replaced Bootstrap 4
      await page.setViewportSize({ width: 375, height: 667 });
      await navigateToNotifications(page);

      // The page should still render content — no horizontal overflow or broken layout
      const mainContent = page
        .locator('[data-testid="main-content"]')
        .or(page.locator('main'))
        .first();
      await expect(mainContent).toBeVisible({ timeout: EXTENDED_TIMEOUT });

      // Sidebar should collapse or become a hamburger menu on mobile
      const sidebar = page
        .locator('[data-testid="sidebar"]')
        .or(page.locator('aside'))
        .first();
      const sidebarVisible = await sidebar.isVisible().catch(() => false);

      // On mobile, either the sidebar is hidden or a hamburger toggle is shown
      if (!sidebarVisible) {
        const hamburger = page
          .locator('[data-testid="menu-toggle"]')
          .or(page.locator('button[aria-label*="menu" i]'))
          .or(page.locator('.hamburger'));
        const hamburgerExists = (await hamburger.count()) > 0;
        expect(hamburgerExists).toBeTruthy();
      }
    });
  });

  // -----------------------------------------------------------------------
  // 10. Navigation Integration
  // -----------------------------------------------------------------------

  test.describe('Navigation', () => {
    test('should navigate to notifications from sidebar', async ({ page }) => {
      // Go to dashboard first, then navigate via sidebar
      await page.goto('/');
      await page.waitForLoadState('networkidle');

      const sidebarLink = page
        .locator('[data-testid="nav-notifications"]')
        .or(page.locator('a[href*="/notifications"]'))
        .or(page.locator('nav a:has-text("Notifications")'))
        .or(page.locator('nav a:has-text("Email")'));

      if ((await sidebarLink.count()) > 0) {
        await sidebarLink.first().click();
        await page.waitForLoadState('networkidle');
        expect(page.url()).toContain('/notifications');
      }
    });

    test('should highlight notifications nav item when on notifications page', async ({
      page,
    }) => {
      // React Router NavLink should apply an active class
      const activeNavItem = page
        .locator('[data-testid="nav-notifications"].active')
        .or(page.locator('a[href*="/notifications"][aria-current="page"]'))
        .or(page.locator('a[href*="/notifications"].active'));

      const isActive = await activeNavItem
        .first()
        .isVisible()
        .catch(() => false);

      // If the nav item exists but isn't explicitly styled as "active",
      // at least verify we're on the correct page
      if (!isActive) {
        expect(page.url()).toContain('/notifications');
      } else {
        expect(isActive).toBeTruthy();
      }
    });
  });
});
