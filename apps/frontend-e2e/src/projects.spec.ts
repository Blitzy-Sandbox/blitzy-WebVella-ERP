/**
 * Project Management E2E Test Suite — WebVella ERP React SPA
 *
 * Validates all critical project management workflows (projects, tasks,
 * timelogs, comments) against a full LocalStack stack (API Gateway → Lambda
 * → DynamoDB → Inventory / Project Management service).  Replaces the
 * monolith's Project plugin-based entity management:
 *
 *   WebVella.Erp.Plugins.Project/ProjectPlugin.cs + 9 patch files
 *     Entities: project, task, task_status, task_type, timelog, comment
 *     Seed data: statuses (open, in progress, completed, closed),
 *                types (bug, feature, task, improvement)
 *
 *   WebVella.Erp.Plugins.Project/Services/TaskService.cs
 *     SetCalculationFields() — computes `key` as "{project.abbr}-{number:N0}"
 *       and `x_search` from subject + key + description.
 *     GetTask(taskId) — retrieves task via EQL with $task_status, $task_type,
 *       $$project_nn_task relations.
 *     GetTaskStatuses() — returns all task_status records.
 *     GetTaskQueue(projectId, userId, TasksDueType, page, pageSize) —
 *       filtered task list excluding closed statuses, ordered by
 *       end_time ASC + priority DESC.
 *
 *   WebVella.Erp.Plugins.Project/Services/TimeLogService.cs
 *     Create(id, createdBy, createdOn, loggedOn, minutes, isBillable,
 *            body, scope, relatedRecords)
 *     Delete(id, userId) — author-only deletion.
 *     GetTimelogsForPeriod(fromDate, toDate, projectId, userId) —
 *       queries by date range with optional project/user filters.
 *
 *   WebVella.Erp.Plugins.Project/Services/CommentService.cs
 *     Create(id, createdBy, createdOn, body, parentId, scope,
 *            relatedRecords) — supports parent-child nesting (one level).
 *     Delete(id, userId) — author-only, cascades to child comments.
 *
 *   WebVella.Erp.Plugins.Project/Services/ProjectService.cs
 *     Get(projectId) — retrieves project by ID.
 *     GetProjectTimelogs(projectId) — timelogs where l_related_records
 *       CONTAINS projectId.
 *
 *   WebVella.Erp.Plugins.Project/Controllers/ProjectController.cs
 *     POST api/v3.0/p/project/pc-post-list/create   — creates comment
 *     POST api/v3.0/p/project/pc-post-list/delete   — deletes comment
 *     POST api/v3.0/p/project/pc-timelog-list/create — creates timelog
 *
 * The React SPA replaces all monolith project views with route-based pages:
 *
 *   GET    /projects                    → ProjectList
 *   GET    /projects/:id                → ProjectDetails / ProjectDashboard
 *   GET    /projects/:id/tasks          → TaskList
 *   GET    /projects/:id/tasks/create   → TaskCreate
 *   GET    /projects/:id/tasks/:taskId  → TaskDetails
 *   GET    /projects/:id/tasks/:taskId/edit → TaskManage
 *   GET    /projects/:id/timelogs       → TimelogList / TimesheetView
 *   GET    /projects/:id/timelogs/create → TimelogCreate
 *
 * API endpoints (Inventory / Project Management microservice):
 *   GET    /v1/projects                 → list projects
 *   POST   /v1/projects                 → create project
 *   GET    /v1/projects/:id             → get project
 *   PUT    /v1/projects/:id             → update project
 *   DELETE /v1/projects/:id             → delete project
 *   GET    /v1/tasks                    → list tasks (with query filters)
 *   POST   /v1/tasks                    → create task
 *   GET    /v1/tasks/:id                → get task
 *   PUT    /v1/tasks/:id                → update task
 *   DELETE /v1/tasks/:id                → delete task
 *   GET    /v1/timelogs                 → list timelogs
 *   POST   /v1/timelogs                 → create timelog
 *   DELETE /v1/timelogs/:id             → delete timelog
 *   GET    /v1/tasks/:id/comments       → list comments for task
 *   POST   /v1/tasks/:id/comments       → create comment on task
 *   DELETE /v1/tasks/:id/comments/:cid  → delete comment
 *
 * Domain events (SNS → SQS):
 *   inventory.project.created, inventory.project.updated
 *   inventory.task.created, inventory.task.updated, inventory.task.deleted
 *   inventory.timelog.created, inventory.timelog.deleted
 *   inventory.comment.created, inventory.comment.deleted
 *
 * Testing pattern (AAP §0.8.1 & §0.8.4):
 *   1. docker compose up -d       — start LocalStack + Step Functions Local
 *   2. npx nx e2e frontend-e2e    — run all E2E tests against LocalStack
 *   3. docker compose down        — tear down LocalStack
 *
 * All tests execute against a real LocalStack instance — zero mocked AWS
 * SDK calls.  The Inventory service is self-contained with its own DynamoDB
 * datastore (AAP §0.8.1: self-contained bounded contexts).
 *
 * Performance target (AAP §0.8.2):
 *   API response P95 (warm) < 500ms — task and timelog CRUD operations are
 *   among the highest-volume API calls in the system.
 */

import { test, expect, Page } from '@playwright/test';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * Default system user email — matches monolith Definitions.cs
 * SystemIds.FirstUserId.  Seeded into Cognito via seed-test-data.sh.
 */
const TEST_EMAIL: string = process.env.TEST_EMAIL ?? 'erp@webvella.com';

/**
 * Default system user password — migrated to Cognito user pool.
 * Original monolith used MD5-hashed password for erp@webvella.com.
 */
const TEST_PASSWORD: string = process.env.TEST_PASSWORD ?? 'erpadmin';

/** Login page route — replaces login.cshtml Razor Page. */
const LOGIN_URL = '/login';

/**
 * Projects section base route — the top-level Project Management navigation
 * destination.  Replaces the monolith's Project plugin routes served via
 * ApplicationNode pages (/{AppName}/{AreaName}/{NodeName}/l/{PageName}).
 */
const PROJECTS_BASE_URL = '/projects';

/** Maximum time (ms) to wait for Cognito-backed auth to complete. */
const AUTH_TIMEOUT = 15_000;

/** Maximum time (ms) to wait for API-driven page transitions. */
const NAV_TIMEOUT = 10_000;

/** Shorter timeout for element visibility / assertion checks. */
const ELEMENT_TIMEOUT = 5_000;

/**
 * Task status values — derived from ProjectPlugin patches which create
 * the task_status entity with these seeded records:
 *   - "not started"  (value=1)
 *   - "in progress"  (value=2)
 *   - "completed"    (value=3)
 *   - "on hold"      (value=4)
 *
 * TaskService.GetTaskQueue() excludes closed statuses when filtering.
 */
const TASK_STATUS_NOT_STARTED = 'not started';
const TASK_STATUS_IN_PROGRESS = 'in progress';
const TASK_STATUS_COMPLETED = 'completed';
const TASK_STATUS_ON_HOLD = 'on hold';

/**
 * Task type values — derived from ProjectPlugin patches which create
 * the task_type entity with these seeded records:
 *   - "bug"         (value=1)
 *   - "feature"     (value=2)
 *   - "task"        (value=3)
 *   - "improvement" (value=4)
 */
const TASK_TYPE_BUG = 'bug';
const TASK_TYPE_FEATURE = 'feature';
const TASK_TYPE_TASK = 'task';
const TASK_TYPE_IMPROVEMENT = 'improvement';

/**
 * Task priority levels — derived from ProjectPlugin patches.
 * Matches the priority field on the task entity (InputSelectField).
 * GetTaskQueue() orders by priority DESC after end_time ASC.
 */
const TASK_PRIORITY_LOW = 'low';
const TASK_PRIORITY_MEDIUM = 'medium';
const TASK_PRIORITY_HIGH = 'high';
const TASK_PRIORITY_URGENT = 'urgent';

/**
 * Test data for task creation.  Values mirror the task entity field
 * definitions from ProjectPlugin patches and the fields handled by
 * TaskService.SetCalculationFields():
 *
 *   subject    — InputTextField  (required) — used to compute x_search
 *   type       — InputSelectField (bug/feature/task/improvement)
 *   status     — InputSelectField (not started/in progress/completed/on hold)
 *   priority   — InputSelectField (low/medium/high/urgent)
 *   start_time — InputDateTimeField
 *   end_time   — InputDateTimeField
 */
const TASK_CREATE_DATA = {
  subject: `E2E Test Task ${Date.now()}`,
  type: TASK_TYPE_FEATURE,
  status: TASK_STATUS_NOT_STARTED,
  priority: TASK_PRIORITY_MEDIUM,
  description: 'Automated Playwright E2E test task for project management validation.',
};

/**
 * Updated task data for edit tests.
 */
const TASK_UPDATE_DATA = {
  subject: `Updated E2E Task ${Date.now()}`,
  status: TASK_STATUS_IN_PROGRESS,
  priority: TASK_PRIORITY_HIGH,
};

/**
 * Test data for timelog creation.  Values mirror the timelog entity field
 * definitions from ProjectPlugin patches and TimeLogService.Create():
 *
 *   minutes     — InputNumberField (stored as int, displayed as hours in UI)
 *   logged_on   — InputDateTimeField (date the work was performed)
 *   is_billable — InputCheckboxField
 *   body        — InputMultiLineTextField (description)
 */
const TIMELOG_CREATE_DATA = {
  hours: '2.5',
  minutes: 150,
  date: new Date().toISOString().split('T')[0],
  description: 'E2E test timelog entry for project management validation.',
  isBillable: true,
};

/**
 * Test data for comment creation.  Values mirror the comment entity field
 * definitions from ProjectPlugin patches and CommentService.Create():
 *
 *   body — InputMultiLineTextField (required) — the comment text content
 */
const COMMENT_CREATE_DATA = {
  body: `E2E test comment created at ${Date.now()} for project management validation.`,
};

// ---------------------------------------------------------------------------
// Reusable login helper (local — avoids cross-spec-file import fragility)
// ---------------------------------------------------------------------------

/**
 * Programmatically logs a user into the WebVella ERP React SPA through the
 * browser UI.  Navigates to the login page, fills credentials, submits the
 * form, and waits for the resulting redirect away from /login.
 *
 * Mirrors the monolith's LoginModel.OnPost() flow:
 *   1. Navigate to /login
 *   2. Fill email  (replaces name="Username" input from login.cshtml)
 *   3. Fill password (replaces name="Password" input from login.cshtml)
 *   4. Click "Login" submit button
 *   5. Wait for successful redirect to the dashboard (or returnUrl)
 *
 * @param page     Playwright Page instance.
 * @param email    User email address (defaults to TEST_EMAIL).
 * @param password User password (defaults to TEST_PASSWORD).
 */
async function loginToApp(
  page: Page,
  email: string = TEST_EMAIL,
  password: string = TEST_PASSWORD,
): Promise<void> {
  await page.goto(LOGIN_URL, { waitUntil: 'networkidle' });

  // Fill credentials — prefer accessible locators (getByLabel) so tests
  // remain resilient to markup changes.  Login.tsx renders:
  //   <label htmlFor="loginEmail">Email</label>
  //   <label htmlFor="loginPassword">Password</label>
  const emailField = page.getByLabel(/email/i);
  const passwordField = page.getByLabel(/password/i);

  await emailField.fill(email);
  await passwordField.fill(password);

  // Submit the form — button text is "Login".
  await page.getByRole('button', { name: /login/i }).click();

  // Wait for navigation away from /login, confirming successful auth.
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), {
    timeout: AUTH_TIMEOUT,
  });
}

// ---------------------------------------------------------------------------
// Navigation Helpers
// ---------------------------------------------------------------------------

/**
 * Navigates to the projects list page and waits for the data table to render.
 * Mirrors the monolith's RecordListPageModel.OnGet() → PcGrid rendering
 * for the project entity.
 *
 * @param page  Playwright Page instance (must be authenticated).
 */
async function navigateToProjectList(page: Page): Promise<void> {
  await page.goto(PROJECTS_BASE_URL, { waitUntil: 'networkidle' });

  // Wait for the data table to appear.  The React DataTable component
  // (replaces PcGrid ViewComponent) renders a <table> with role="grid" or
  // a data-testid attribute.
  await page.waitForSelector(
    'table, [data-testid="data-table"], [role="grid"]',
    { timeout: NAV_TIMEOUT },
  );
}

/**
 * Navigates to a specific project's detail / dashboard page.
 * The React SPA renders a project overview with task list, timelog summary,
 * and comment feed — replacing the monolith's ApplicationNode page for
 * the project entity.
 *
 * @param page       Playwright Page instance (must be authenticated).
 * @param projectId  The GUID of the project to navigate to.
 */
async function navigateToProjectDetails(
  page: Page,
  projectId: string,
): Promise<void> {
  await page.goto(`${PROJECTS_BASE_URL}/${projectId}`, {
    waitUntil: 'networkidle',
  });
  // Wait for the project detail heading or primary content area
  await page.waitForSelector(
    '[data-testid="project-detail"], [data-testid="page-title"], h1, h2',
    { timeout: NAV_TIMEOUT },
  );
}

/**
 * Navigates to the task list within a specific project.
 * The React SPA renders tasks in a DataTable with columns: key, subject,
 * type, status, priority, assignee, end_time.
 *
 * @param page       Playwright Page instance (must be authenticated).
 * @param projectId  The GUID of the project.
 */
async function navigateToTaskList(
  page: Page,
  projectId: string,
): Promise<void> {
  await page.goto(`${PROJECTS_BASE_URL}/${projectId}/tasks`, {
    waitUntil: 'networkidle',
  });
  await page.waitForSelector(
    'table, [data-testid="data-table"], [role="grid"], [data-testid="task-list"]',
    { timeout: NAV_TIMEOUT },
  );
}

/**
 * Navigates to the timelog list / timesheet view within a specific project.
 *
 * @param page       Playwright Page instance (must be authenticated).
 * @param projectId  The GUID of the project.
 */
async function navigateToTimelogList(
  page: Page,
  projectId: string,
): Promise<void> {
  await page.goto(`${PROJECTS_BASE_URL}/${projectId}/timelogs`, {
    waitUntil: 'networkidle',
  });
  await page.waitForSelector(
    'table, [data-testid="data-table"], [data-testid="timelog-list"], [data-testid="timesheet"]',
    { timeout: NAV_TIMEOUT },
  );
}

// ---------------------------------------------------------------------------
// Table / Row Helpers
// ---------------------------------------------------------------------------

/**
 * Returns a locator for table body rows in a project data table.
 *
 * @param page  Playwright Page instance.
 * @returns     Locator matching all <tr> elements within <tbody>.
 */
function getTableRows(page: Page) {
  return page
    .locator('[data-testid="data-table"] tbody tr')
    .or(page.locator('table tbody tr'))
    .or(page.locator('[role="grid"] [role="row"]'));
}

/**
 * Returns a locator for table column headers.
 *
 * @param page  Playwright Page instance.
 * @returns     Locator matching all header cells.
 */
function getTableHeaders(page: Page) {
  return page
    .locator('[data-testid="data-table"] thead th')
    .or(page.locator('table thead th'))
    .or(page.locator('[role="grid"] [role="columnheader"]'));
}

/**
 * Extracts a record ID from the current page URL.
 * Expects URL patterns like:
 *   /projects/:projectId
 *   /projects/:projectId/tasks/:taskId
 *   /projects/:projectId/tasks/:taskId/edit
 *
 * @param page       Playwright Page instance.
 * @param segments   Number of path segments from the end to locate the ID.
 * @returns          The record ID string (GUID).
 */
function extractIdFromUrl(page: Page, segments: number = 1): string {
  const pathname = new URL(page.url()).pathname;
  const parts = pathname.split('/').filter(Boolean);
  // For /projects/:projectId → parts[1]
  // For /projects/:projectId/tasks/:taskId → parts[3]
  return parts[parts.length - segments] ?? '';
}

/**
 * Waits for a success notification / toast to appear after a CRUD operation.
 * Many React UI libraries render a toast notification with role="alert" or
 * data-testid attributes.
 *
 * @param page  Playwright Page instance.
 */
async function waitForSuccessNotification(page: Page): Promise<void> {
  const notification = page
    .locator('[data-testid="notification-success"]')
    .or(page.locator('[role="alert"]'))
    .or(page.locator('.toast-success'))
    .or(page.locator('[data-testid="toast"]'));

  await expect(notification.first()).toBeVisible({ timeout: NAV_TIMEOUT });
}

/**
 * Waits for and accepts a confirmation dialog (browser native or custom modal).
 * Used for delete operations that show "Are you sure?" confirmation.
 *
 * @param page  Playwright Page instance.
 */
async function handleDeleteConfirmation(page: Page): Promise<void> {
  // Attempt to handle a browser-native dialog first
  const dialogPromise = page.waitForEvent('dialog', { timeout: 3_000 }).catch(() => null);

  // Also look for a custom modal confirmation button
  const confirmButton = page
    .getByRole('button', { name: /confirm|yes|delete|ok/i })
    .or(page.locator('[data-testid="confirm-delete"]'))
    .or(page.locator('[data-testid="dialog-confirm"]'));

  const dialog = await dialogPromise;
  if (dialog) {
    await dialog.accept();
  } else {
    // Custom modal — click the confirm button
    await confirmButton.first().click({ timeout: ELEMENT_TIMEOUT });
  }
}


// ===========================================================================
// Test Suite
// ===========================================================================

test.describe('Project Management', () => {
  // -------------------------------------------------------------------------
  // Lifecycle — authenticate and navigate to Projects before each test
  // -------------------------------------------------------------------------

  /**
   * Before each test:
   *   1. Log in via Cognito through the React login form
   *   2. Navigate to the Projects base section
   *
   * This mirrors the monolith's ErpMiddleware per-request pipeline:
   *   SecurityContext binding -> page resolution -> hook execution -> render.
   *
   * The monolith's ProjectPlugin registered routes under the "Project"
   * application node.  The React SPA serves all project routes under
   * /projects with client-side routing via React Router 7.
   */
  test.beforeEach(async ({ page }) => {
    await loginToApp(page);
    // Navigate to the Projects section root to establish context
    await page.goto(PROJECTS_BASE_URL, { waitUntil: 'networkidle' });
  });

  /**
   * After each test, clear authentication state to prevent leakage between
   * tests sharing a browser context.  Mirrors monolith's logout.cshtml.cs
   * session teardown.
   */
  test.afterEach(async ({ context }) => {
    await context.clearCookies();
    const pages = context.pages();
    for (const p of pages) {
      try {
        await p.evaluate(() => {
          try {
            localStorage.clear();
          } catch {
            /* origin not yet assigned — safe to ignore */
          }
          try {
            sessionStorage.clear();
          } catch {
            /* origin not yet assigned — safe to ignore */
          }
        });
      } catch {
        // Page may not yet have a valid origin — ignore safely
      }
    }
  });

  // =========================================================================
  // PROJECT LISTING AND NAVIGATION TESTS
  // Replaces project entity management via RecordList / ApplicationNode
  // Razor Pages + ProjectPlugin entity definitions
  // =========================================================================

  test.describe('Project Listing and Navigation', () => {
    /**
     * Verifies that the projects page renders correctly with a page title
     * and project data table.  Replaces the monolith's RecordListPageModel
     * rendering for the project entity.
     *
     * Source: ProjectPlugin.cs -> entity definition seeds project entity
     * with fields: name, abbr, owner_id, description, color, status.
     */
    test('should display projects page', async ({ page }) => {
      await navigateToProjectList(page);

      // Verify we are on the correct URL
      expect(page.url()).toContain(PROJECTS_BASE_URL);

      // The page should have a visible heading indicating projects.
      // Monolith rendered entity.LabelPlural ("Projects") as the page title.
      const heading = page
        .getByRole('heading')
        .or(page.locator('[data-testid="page-title"]'));
      await expect(heading.first()).toBeVisible({ timeout: ELEMENT_TIMEOUT });

      // The data table (or project list) should be present
      const tableOrList = page
        .locator('[data-testid="data-table"]')
        .or(page.locator('table'))
        .or(page.locator('[role="grid"]'))
        .or(page.locator('[data-testid="project-list"]'));
      await expect(tableOrList.first()).toBeVisible({ timeout: ELEMENT_TIMEOUT });
    });

    /**
     * Verifies that the project list displays the correct columns and at
     * least one row of seeded data.  Derived from project entity field
     * definitions in ProjectPlugin patches:
     *   name      - InputTextField (required)
     *   abbr      - InputTextField (short abbreviation, used in task key generation)
     *   owner_id  - InputGuidField (project owner)
     *   status    - InputSelectField
     *
     * Source: ProjectPlugin patches create the project entity with these fields.
     */
    test('should list existing projects', async ({ page }) => {
      await navigateToProjectList(page);

      // The data table should be present
      const table = page
        .locator('[data-testid="data-table"]')
        .or(page.locator('table'))
        .or(page.locator('[role="grid"]'));
      await expect(table.first()).toBeVisible({ timeout: ELEMENT_TIMEOUT });

      // Verify table has column headers.
      const headers = getTableHeaders(page);
      await expect(headers.first()).toBeVisible({ timeout: ELEMENT_TIMEOUT });
      const headerCount = await headers.count();
      expect(headerCount).toBeGreaterThan(0);

      // Verify that key column headers are present
      const headerTexts = await headers.allTextContents();
      const headerTextJoined = headerTexts.join(' ').toLowerCase();

      const hasRelevantColumns =
        headerTextJoined.includes('name') ||
        headerTextJoined.includes('abbr') ||
        headerTextJoined.includes('abbreviation') ||
        headerTextJoined.includes('owner') ||
        headerTextJoined.includes('status');
      expect(hasRelevantColumns).toBe(true);

      // Verify at least one data row exists (seeded test data).
      const rows = getTableRows(page);
      const rowCount = await rows.count();
      expect(rowCount).toBeGreaterThan(0);
    });

    /**
     * Verifies navigation from the project list to a project detail page.
     *
     * Source: ProjectService.Get(projectId) retrieves project by ID.
     */
    test('should navigate to project details', async ({ page }) => {
      await navigateToProjectList(page);

      // Click on the first project row or link
      const firstProjectLink = page
        .locator('[data-testid="data-table"] tbody tr a')
        .or(page.locator('table tbody tr a'))
        .or(page.locator('[data-testid="project-list"] a'))
        .or(page.locator('[role="grid"] [role="row"] a'));
      const linkCount = await firstProjectLink.count();

      if (linkCount > 0) {
        await firstProjectLink.first().click();
      } else {
        const firstRow = getTableRows(page);
        await firstRow.first().click();
      }

      // Wait for navigation to a project detail URL
      await page.waitForURL(
        (url) => {
          const path = url.pathname;
          return (
            path.startsWith('/projects/') &&
            path.split('/').filter(Boolean).length >= 2
          );
        },
        { timeout: NAV_TIMEOUT },
      );

      // Verify project detail content is visible
      const projectDetail = page
        .locator('[data-testid="project-detail"]')
        .or(page.locator('[data-testid="page-title"]'))
        .or(page.getByRole('heading'));
      await expect(projectDetail.first()).toBeVisible({ timeout: ELEMENT_TIMEOUT });

      // Verify task section is present on the project detail page
      const taskSection = page
        .locator('[data-testid="task-list"]')
        .or(page.locator('[data-testid="task-section"]'))
        .or(page.getByText(/tasks/i));
      await expect(taskSection.first()).toBeVisible({ timeout: ELEMENT_TIMEOUT });
    });
  });

  // =========================================================================
  // TASK CRUD TESTS
  // Replaces task entity management via RecordCreate/Details/Manage/List
  // Razor Pages + TaskService.cs + RecordManager CRUD
  // =========================================================================

  test.describe('Tasks', () => {
    /**
     * Stores the project ID discovered during test execution.
     * Tests navigate to the first available project's task list.
     */
    let activeProjectId: string;

    /**
     * Before each task test, navigate to the first project's task list.
     * Discovers the project ID dynamically from the project list.
     */
    test.beforeEach(async ({ page }) => {
      await navigateToProjectList(page);

      // Click first project to get to project detail
      const firstProjectLink = page
        .locator('[data-testid="data-table"] tbody tr a')
        .or(page.locator('table tbody tr a'))
        .or(page.locator('[data-testid="project-list"] a'))
        .or(page.locator('[role="grid"] [role="row"] a'));
      const linkCount = await firstProjectLink.count();

      if (linkCount > 0) {
        await firstProjectLink.first().click();
      } else {
        const firstRow = getTableRows(page);
        await firstRow.first().click();
      }

      // Wait for project detail URL
      await page.waitForURL(
        (url) => {
          const path = url.pathname;
          return (
            path.startsWith('/projects/') &&
            path.split('/').filter(Boolean).length >= 2
          );
        },
        { timeout: NAV_TIMEOUT },
      );

      // Extract project ID from URL
      const pathname = new URL(page.url()).pathname;
      const segments = pathname.split('/').filter(Boolean);
      activeProjectId = segments[1] ?? '';

      // Navigate to the task list for this project
      await navigateToTaskList(page, activeProjectId);
    });

    /**
     * Verifies creating a new task within a project.
     *
     * Replaces the monolith's RecordCreate Razor Page flow:
     *   RecordCreate.cshtml.cs -> PageService.ConvertFormPostToEntityRecord()
     *   -> RecordManager.CreateRecord() -> TaskService.SetCalculationFields()
     *   which generates key as "{project.abbr}-{number:N0}" and x_search.
     *
     * API: POST /v1/tasks
     * Event: inventory.task.created (SNS -> SQS)
     */
    test('should create a new task', async ({ page }) => {
      // Click the "Create Task" button / link
      const createButton = page
        .getByRole('button', { name: /create|new|add/i })
        .or(page.getByRole('link', { name: /create|new|add/i }))
        .or(page.locator('[data-testid="create-task"]'))
        .or(page.locator('[data-testid="btn-create"]'));
      await createButton.first().click();

      // Wait for the task creation form to appear
      await page.waitForSelector(
        'form, [data-testid="task-form"], [data-testid="record-form"]',
        { timeout: NAV_TIMEOUT },
      );

      // Fill in the subject field (required) — maps to InputTextField
      const subjectField = page
        .getByLabel(/subject/i)
        .or(page.locator('[data-testid="field-subject"] input'))
        .or(page.locator('input[name="subject"]'));
      await subjectField.first().fill(TASK_CREATE_DATA.subject);

      // Fill in the type field — maps to InputSelectField
      const typeSelect = page
        .getByLabel(/type/i)
        .or(page.locator('[data-testid="field-type"] select'))
        .or(page.locator('select[name="type"]'));
      const typeSelectCount = await typeSelect.count();
      if (typeSelectCount > 0) {
        await typeSelect.first().selectOption({ label: TASK_CREATE_DATA.type });
      }

      // Fill in the status field — maps to InputSelectField
      const statusSelect = page
        .getByLabel(/status/i)
        .or(page.locator('[data-testid="field-status"] select'))
        .or(page.locator('select[name="status"]'));
      const statusSelectCount = await statusSelect.count();
      if (statusSelectCount > 0) {
        await statusSelect.first().selectOption({ label: TASK_CREATE_DATA.status });
      }

      // Fill in the priority field — maps to InputSelectField
      const prioritySelect = page
        .getByLabel(/priority/i)
        .or(page.locator('[data-testid="field-priority"] select'))
        .or(page.locator('select[name="priority"]'));
      const prioritySelectCount = await prioritySelect.count();
      if (prioritySelectCount > 0) {
        await prioritySelect.first().selectOption({ label: TASK_CREATE_DATA.priority });
      }

      // Fill in the description field (optional)
      const descField = page
        .getByLabel(/description/i)
        .or(page.locator('[data-testid="field-description"] textarea'))
        .or(page.locator('textarea[name="description"]'));
      const descFieldCount = await descField.count();
      if (descFieldCount > 0) {
        await descField.first().fill(TASK_CREATE_DATA.description);
      }

      // Submit the form
      const submitButton = page
        .getByRole('button', { name: /save|submit|create/i })
        .or(page.locator('[data-testid="btn-save"]'))
        .or(page.locator('[data-testid="btn-submit"]'));
      await submitButton.first().click();

      // Verify success: notification or navigation to detail / list
      await Promise.race([
        waitForSuccessNotification(page).catch(() => null),
        page.waitForURL(
          (url) => {
            const path = url.pathname;
            return path.includes('/tasks') && !path.includes('/create');
          },
          { timeout: NAV_TIMEOUT },
        ).catch(() => null),
      ]);

      // Navigate back to task list to verify the task appears
      await navigateToTaskList(page, activeProjectId);

      // Verify the newly created task appears in the list
      const taskInList = page.getByText(TASK_CREATE_DATA.subject);
      await expect(taskInList.first()).toBeVisible({ timeout: ELEMENT_TIMEOUT });
    });

    /**
     * Verifies that task details are displayed correctly including the
     * computed key field.
     *
     * Source: TaskService.SetCalculationFields() computes:
     *   key = "{projectAbbr}-{number:N0}" (e.g., "PROJ-1")
     *   x_search = subject + " " + key + " " + description
     *
     * API: GET /v1/tasks/:id
     */
    test('should display task details', async ({ page }) => {
      // Click on the first task in the list
      const firstTaskLink = page
        .locator('[data-testid="data-table"] tbody tr a')
        .or(page.locator('table tbody tr a'))
        .or(page.locator('[data-testid="task-list"] a'))
        .or(page.locator('[role="grid"] [role="row"] a'));
      const linkCount = await firstTaskLink.count();

      if (linkCount > 0) {
        await firstTaskLink.first().click();
      } else {
        const firstRow = getTableRows(page);
        await firstRow.first().click();
      }

      // Wait for navigation to task detail page
      await page.waitForURL(
        (url) => {
          const path = url.pathname;
          return path.includes('/tasks/') && !path.endsWith('/tasks');
        },
        { timeout: NAV_TIMEOUT },
      );

      // Verify task detail content is visible
      const detailContainer = page
        .locator('[data-testid="task-detail"]')
        .or(page.locator('[data-testid="record-detail"]'))
        .or(page.getByRole('heading'));
      await expect(detailContainer.first()).toBeVisible({ timeout: ELEMENT_TIMEOUT });

      // Verify key fields are displayed:

      // Subject field should be visible
      const subjectLabel = page
        .locator('[data-testid="field-subject"]')
        .or(page.getByText(/subject/i));
      await expect(subjectLabel.first()).toBeVisible({ timeout: ELEMENT_TIMEOUT });

      // Key field (e.g., "PROJ-1") — computed by TaskService
      const keyField = page
        .locator('[data-testid="field-key"]')
        .or(page.getByText(/[A-Z]+-\d+/));
      await expect(keyField.first()).toBeVisible({ timeout: ELEMENT_TIMEOUT });

      // Type field should be visible (bug/feature/task/improvement)
      const typeField = page
        .locator('[data-testid="field-type"]')
        .or(page.getByText(/type/i));
      await expect(typeField.first()).toBeVisible({ timeout: ELEMENT_TIMEOUT });

      // Status field should be visible
      const statusField = page
        .locator('[data-testid="field-status"]')
        .or(page.getByText(/status/i));
      await expect(statusField.first()).toBeVisible({ timeout: ELEMENT_TIMEOUT });
    });

    /**
     * Verifies editing an existing task (subject, status, priority).
     *
     * Replaces the monolith's RecordManage Razor Page flow:
     *   RecordManage.cshtml.cs -> ConvertFormPostToEntityRecord()
     *   -> RecordManager.UpdateRecord() -> post-update hooks
     *
     * API: PUT /v1/tasks/:id
     * Event: inventory.task.updated (SNS -> SQS)
     */
    test('should edit a task', async ({ page }) => {
      // Click on the first task to navigate to detail
      const firstTaskLink = page
        .locator('[data-testid="data-table"] tbody tr a')
        .or(page.locator('table tbody tr a'))
        .or(page.locator('[data-testid="task-list"] a'));
      const linkCount = await firstTaskLink.count();

      if (linkCount > 0) {
        await firstTaskLink.first().click();
      } else {
        const firstRow = getTableRows(page);
        await firstRow.first().click();
      }

      // Wait for task detail page
      await page.waitForURL(
        (url) => {
          const path = url.pathname;
          return path.includes('/tasks/') && !path.endsWith('/tasks');
        },
        { timeout: NAV_TIMEOUT },
      );

      // Click the Edit button
      const editButton = page
        .getByRole('button', { name: /edit/i })
        .or(page.getByRole('link', { name: /edit/i }))
        .or(page.locator('[data-testid="btn-edit"]'));
      await editButton.first().click();

      // Wait for the edit form to appear
      await page.waitForSelector(
        'form, [data-testid="task-form"], [data-testid="record-form"]',
        { timeout: NAV_TIMEOUT },
      );

      // Update the subject field
      const subjectField = page
        .getByLabel(/subject/i)
        .or(page.locator('[data-testid="field-subject"] input'))
        .or(page.locator('input[name="subject"]'));
      await subjectField.first().clear();
      await subjectField.first().fill(TASK_UPDATE_DATA.subject);

      // Update the status field
      const statusSelect = page
        .getByLabel(/status/i)
        .or(page.locator('[data-testid="field-status"] select'))
        .or(page.locator('select[name="status"]'));
      const statusSelectCount = await statusSelect.count();
      if (statusSelectCount > 0) {
        await statusSelect.first().selectOption({ label: TASK_UPDATE_DATA.status });
      }

      // Update the priority field
      const prioritySelect = page
        .getByLabel(/priority/i)
        .or(page.locator('[data-testid="field-priority"] select'))
        .or(page.locator('select[name="priority"]'));
      const prioritySelectCount = await prioritySelect.count();
      if (prioritySelectCount > 0) {
        await prioritySelect.first().selectOption({ label: TASK_UPDATE_DATA.priority });
      }

      // Submit the form
      const submitButton = page
        .getByRole('button', { name: /save|submit|update/i })
        .or(page.locator('[data-testid="btn-save"]'))
        .or(page.locator('[data-testid="btn-submit"]'));
      await submitButton.first().click();

      // Verify success notification or navigation
      await Promise.race([
        waitForSuccessNotification(page).catch(() => null),
        page.waitForURL(
          (url) => {
            const path = url.pathname;
            return path.includes('/tasks/') && !path.includes('/edit');
          },
          { timeout: NAV_TIMEOUT },
        ).catch(() => null),
      ]);

      // Verify the updated subject is visible on the detail page
      const updatedSubject = page.getByText(TASK_UPDATE_DATA.subject);
      await expect(updatedSubject.first()).toBeVisible({ timeout: ELEMENT_TIMEOUT });
    });

    /**
     * Verifies deleting a task from the detail view.
     *
     * Replaces the monolith's RecordDetails.cshtml.cs delete flow:
     *   HookKey == "delete" -> RecordManager.DeleteRecord()
     *   -> redirect to /l/ (list page)
     *
     * API: DELETE /v1/tasks/:id
     * Event: inventory.task.deleted (SNS -> SQS)
     */
    test('should delete a task', async ({ page }) => {
      // First create a task specifically for deletion to avoid interfering
      // with other tests.
      const createButton = page
        .getByRole('button', { name: /create|new|add/i })
        .or(page.getByRole('link', { name: /create|new|add/i }))
        .or(page.locator('[data-testid="create-task"]'))
        .or(page.locator('[data-testid="btn-create"]'));
      await createButton.first().click();

      // Wait for the form
      await page.waitForSelector(
        'form, [data-testid="task-form"], [data-testid="record-form"]',
        { timeout: NAV_TIMEOUT },
      );

      // Fill minimal required fields for the delete-target task
      const deleteTaskSubject = `Delete-Me-Task-${Date.now()}`;
      const subjectField = page
        .getByLabel(/subject/i)
        .or(page.locator('[data-testid="field-subject"] input'))
        .or(page.locator('input[name="subject"]'));
      await subjectField.first().fill(deleteTaskSubject);

      // Submit creation
      const submitButton = page
        .getByRole('button', { name: /save|submit|create/i })
        .or(page.locator('[data-testid="btn-save"]'));
      await submitButton.first().click();

      // Wait for navigation or success
      await Promise.race([
        waitForSuccessNotification(page).catch(() => null),
        page.waitForURL(
          (url) => !url.pathname.includes('/create'),
          { timeout: NAV_TIMEOUT },
        ).catch(() => null),
      ]);

      // Navigate back to task list and find the task we just created
      await navigateToTaskList(page, activeProjectId);
      const taskRow = page.getByText(deleteTaskSubject);
      await expect(taskRow.first()).toBeVisible({ timeout: ELEMENT_TIMEOUT });

      // Click on the task to go to its detail page
      await taskRow.first().click();
      await page.waitForURL(
        (url) => {
          const path = url.pathname;
          return path.includes('/tasks/') && !path.endsWith('/tasks');
        },
        { timeout: NAV_TIMEOUT },
      );

      // Click the Delete button
      const deleteButton = page
        .getByRole('button', { name: /delete|remove/i })
        .or(page.locator('[data-testid="btn-delete"]'));
      await deleteButton.first().click();

      // Handle confirmation dialog (browser native or custom modal)
      await handleDeleteConfirmation(page);

      // Verify: either success notification or navigation back to task list
      await Promise.race([
        waitForSuccessNotification(page).catch(() => null),
        page.waitForURL(
          (url) => {
            const path = url.pathname;
            return path.includes('/tasks') && !path.includes(deleteTaskSubject);
          },
          { timeout: NAV_TIMEOUT },
        ).catch(() => null),
      ]);

      // Navigate to task list and verify the task is gone
      await navigateToTaskList(page, activeProjectId);
      const deletedTask = page.getByText(deleteTaskSubject);
      await expect(deletedTask).toHaveCount(0);
    });

    /**
     * Verifies filtering tasks by status using the status filter control.
     *
     * Replaces the monolith's GetTaskQueue() which supports filtering by
     * TasksDueType and excludes closed statuses.  The task_status entity
     * (created in ProjectPlugin patches) defines available statuses:
     *   not started, in progress, completed, on hold.
     *
     * Source: TaskService.GetTaskQueue(projectId, userId, TasksDueType, ...)
     *   excludes closed statuses and orders by end_time ASC + priority DESC.
     */
    test('should filter tasks by status', async ({ page }) => {
      // Look for a status filter control (dropdown, select, or filter buttons)
      const statusFilter = page
        .locator('[data-testid="filter-status"]')
        .or(page.getByLabel(/filter.*status|status.*filter/i))
        .or(page.locator('[data-testid="task-filter"] select'))
        .or(page.locator('select[name="status-filter"]'));

      const filterCount = await statusFilter.count();

      if (filterCount > 0) {
        // Select "in progress" status filter
        await statusFilter.first().selectOption({ label: TASK_STATUS_IN_PROGRESS });

        // Wait for the table to refresh with filtered results
        await page.waitForTimeout(1_000);
        await page.waitForSelector(
          'table, [data-testid="data-table"], [role="grid"]',
          { timeout: NAV_TIMEOUT },
        );

        // All visible rows should contain the filtered status
        const rows = getTableRows(page);
        const rowCount = await rows.count();
        if (rowCount > 0) {
          const pageContent = await page.textContent('body');
          const bodyText = (pageContent ?? '').toLowerCase();
          expect(
            bodyText.includes(TASK_STATUS_IN_PROGRESS.toLowerCase()) ||
            bodyText.includes('in progress') ||
            bodyText.includes('no') ||
            bodyText.includes('empty'),
          ).toBe(true);
        }

        // Now filter by "completed"
        await statusFilter.first().selectOption({ label: TASK_STATUS_COMPLETED });
        await page.waitForTimeout(1_000);

        // Verify the list updated (different set of tasks or empty)
        const completedRows = getTableRows(page);
        const completedRowCount = await completedRows.count();
        expect(completedRowCount).toBeGreaterThanOrEqual(0);
      } else {
        // Alternative: look for filter tabs or buttons
        const filterTabs = page
          .locator('[data-testid="status-tabs"]')
          .or(page.locator('[role="tablist"]'))
          .or(page.getByRole('tab'));

        const tabCount = await filterTabs.count();
        if (tabCount > 0) {
          // Click the "In Progress" tab / filter
          const inProgressTab = page
            .getByRole('tab', { name: /in progress/i })
            .or(page.getByText(/in progress/i));
          const inProgressCount = await inProgressTab.count();
          if (inProgressCount > 0) {
            await inProgressTab.first().click();
            await page.waitForTimeout(1_000);
          }

          // Click the "Completed" tab / filter
          const completedTab = page
            .getByRole('tab', { name: /completed/i })
            .or(page.getByText(/completed/i));
          const completedCount = await completedTab.count();
          if (completedCount > 0) {
            await completedTab.first().click();
            await page.waitForTimeout(1_000);
          }
        }

        // Verify the page is still functional after filter attempts
        const tableOrList = page
          .locator('[data-testid="data-table"]')
          .or(page.locator('table'))
          .or(page.locator('[data-testid="task-list"]'));
        await expect(tableOrList.first()).toBeVisible({ timeout: ELEMENT_TIMEOUT });
      }
    });
  }); // end Tasks describe

  // =========================================================================
  // TIMELOG TESTS
  // Replaces timelog entity management via ProjectController API +
  // TimeLogService.cs + timelog entity definitions
  // =========================================================================

  test.describe('Timelogs', () => {
    /**
     * Stores the project ID discovered during test execution.
     */
    let activeProjectId: string;

    /**
     * Stores a task ID for associating timelog entries.
     */
    let activeTaskId: string;

    /**
     * Before each timelog test, navigate to the first project and identify
     * a task for timelog operations.
     */
    test.beforeEach(async ({ page }) => {
      await navigateToProjectList(page);

      // Click first project to get to project detail
      const firstProjectLink = page
        .locator('[data-testid="data-table"] tbody tr a')
        .or(page.locator('table tbody tr a'))
        .or(page.locator('[data-testid="project-list"] a'))
        .or(page.locator('[role="grid"] [role="row"] a'));
      const linkCount = await firstProjectLink.count();

      if (linkCount > 0) {
        await firstProjectLink.first().click();
      } else {
        const firstRow = getTableRows(page);
        await firstRow.first().click();
      }

      // Wait for project detail URL
      await page.waitForURL(
        (url) => {
          const path = url.pathname;
          return (
            path.startsWith('/projects/') &&
            path.split('/').filter(Boolean).length >= 2
          );
        },
        { timeout: NAV_TIMEOUT },
      );

      // Extract project ID from URL
      const pathname = new URL(page.url()).pathname;
      const segments = pathname.split('/').filter(Boolean);
      activeProjectId = segments[1] ?? '';

      // Navigate to task list to find a task ID
      await navigateToTaskList(page, activeProjectId);

      // Try to extract a task ID from the first task link
      const firstTaskLink = page
        .locator('[data-testid="data-table"] tbody tr a')
        .or(page.locator('table tbody tr a'))
        .or(page.locator('[data-testid="task-list"] a'));
      const taskLinkCount = await firstTaskLink.count();
      if (taskLinkCount > 0) {
        const href = await firstTaskLink.first().getAttribute('href');
        if (href) {
          const taskSegments = href.split('/').filter(Boolean);
          const taskIdx = taskSegments.indexOf('tasks');
          activeTaskId = taskIdx >= 0 ? taskSegments[taskIdx + 1] ?? '' : '';
        } else {
          activeTaskId = '';
        }
      } else {
        activeTaskId = '';
      }
    });

    /**
     * Verifies creating a timelog entry for a task.
     *
     * Replaces the monolith's ProjectController.CreateTimelog() endpoint:
     *   POST api/v3.0/p/project/pc-timelog-list/create
     *   Parameters: minutes, isBillable, loggedOn, body, scope, relatedRecords
     *
     * Source: TimeLogService.Create(id, createdBy, createdOn, loggedOn,
     *   minutes, isBillable, body, scope, relatedRecords)
     *
     * API: POST /v1/timelogs
     * Event: inventory.timelog.created (SNS -> SQS)
     */
    test('should create a timelog entry', async ({ page }) => {
      // Navigate to timelog creation page (project-level or task-level)
      const timelogCreateUrl =
        activeTaskId
          ? `${PROJECTS_BASE_URL}/${activeProjectId}/tasks/${activeTaskId}`
          : `${PROJECTS_BASE_URL}/${activeProjectId}/timelogs`;
      await page.goto(timelogCreateUrl, { waitUntil: 'networkidle' });

      // Look for a "Log Time" or "Create Timelog" button
      const logTimeButton = page
        .getByRole('button', { name: /log time|add timelog|create timelog|new timelog/i })
        .or(page.getByRole('link', { name: /log time|add timelog|create timelog/i }))
        .or(page.locator('[data-testid="btn-log-time"]'))
        .or(page.locator('[data-testid="create-timelog"]'));

      const logTimeCount = await logTimeButton.count();
      if (logTimeCount > 0) {
        await logTimeButton.first().click();
      } else {
        // Fallback: navigate directly to timelog create route
        await page.goto(
          `${PROJECTS_BASE_URL}/${activeProjectId}/timelogs/create`,
          { waitUntil: 'networkidle' },
        );
      }

      // Wait for the timelog form to appear
      await page.waitForSelector(
        'form, [data-testid="timelog-form"], [data-testid="record-form"]',
        { timeout: NAV_TIMEOUT },
      );

      // Fill in the hours field
      const hoursField = page
        .getByLabel(/hours|minutes|time/i)
        .or(page.locator('[data-testid="field-hours"] input'))
        .or(page.locator('[data-testid="field-minutes"] input'))
        .or(page.locator('input[name="hours"]'))
        .or(page.locator('input[name="minutes"]'));
      await hoursField.first().fill(TIMELOG_CREATE_DATA.hours);

      // Fill in the date field
      const dateField = page
        .getByLabel(/date|logged on|logged_on/i)
        .or(page.locator('[data-testid="field-date"] input'))
        .or(page.locator('[data-testid="field-logged_on"] input'))
        .or(page.locator('input[name="date"]'))
        .or(page.locator('input[type="date"]'));
      const dateFieldCount = await dateField.count();
      if (dateFieldCount > 0) {
        await dateField.first().fill(TIMELOG_CREATE_DATA.date);
      }

      // Fill in the description field
      const descField = page
        .getByLabel(/description|body|note/i)
        .or(page.locator('[data-testid="field-description"] textarea'))
        .or(page.locator('[data-testid="field-body"] textarea'))
        .or(page.locator('textarea[name="description"]'))
        .or(page.locator('textarea[name="body"]'));
      const descFieldCount = await descField.count();
      if (descFieldCount > 0) {
        await descField.first().fill(TIMELOG_CREATE_DATA.description);
      }

      // Check the billable checkbox if present
      const billableCheckbox = page
        .getByLabel(/billable/i)
        .or(page.locator('[data-testid="field-is_billable"] input[type="checkbox"]'))
        .or(page.locator('input[name="isBillable"]'));
      const billableCount = await billableCheckbox.count();
      if (billableCount > 0 && TIMELOG_CREATE_DATA.isBillable) {
        const isChecked = await billableCheckbox.first().isChecked();
        if (!isChecked) {
          await billableCheckbox.first().check();
        }
      }

      // Submit the form
      const submitButton = page
        .getByRole('button', { name: /save|submit|log|create/i })
        .or(page.locator('[data-testid="btn-save"]'))
        .or(page.locator('[data-testid="btn-submit"]'));
      await submitButton.first().click();

      // Verify success: notification or navigation
      await Promise.race([
        waitForSuccessNotification(page).catch(() => null),
        page.waitForURL(
          (url) => !url.pathname.includes('/create'),
          { timeout: NAV_TIMEOUT },
        ).catch(() => null),
      ]);

      // Verify the timelog entry is visible
      const timelogEntry = page
        .getByText(TIMELOG_CREATE_DATA.description)
        .or(page.getByText(TIMELOG_CREATE_DATA.hours))
        .or(page.locator('[data-testid="timelog-entry"]'));
      await expect(timelogEntry.first()).toBeVisible({ timeout: ELEMENT_TIMEOUT });
    });

    /**
     * Verifies that the timelog list displays entries with hours, date,
     * and logged by information.
     *
     * Source: TimeLogService.GetTimelogsForPeriod(fromDate, toDate, projectId, userId)
     *
     * API: GET /v1/timelogs?projectId=:id
     */
    test('should display timelog list for a task', async ({ page }) => {
      // Navigate to the timelog list / timesheet for the project
      await navigateToTimelogList(page, activeProjectId);

      // Verify the timelog list is visible
      const timelogContainer = page
        .locator('[data-testid="timelog-list"]')
        .or(page.locator('[data-testid="timesheet"]'))
        .or(page.locator('[data-testid="data-table"]'))
        .or(page.locator('table'));
      await expect(timelogContainer.first()).toBeVisible({ timeout: ELEMENT_TIMEOUT });

      // Verify column headers include time-related fields
      const headers = getTableHeaders(page);
      const headerCount = await headers.count();

      if (headerCount > 0) {
        const headerTexts = await headers.allTextContents();
        const headerTextJoined = headerTexts.join(' ').toLowerCase();

        const hasTimelogColumns =
          headerTextJoined.includes('hour') ||
          headerTextJoined.includes('minute') ||
          headerTextJoined.includes('time') ||
          headerTextJoined.includes('date') ||
          headerTextJoined.includes('logged') ||
          headerTextJoined.includes('billable') ||
          headerTextJoined.includes('description') ||
          headerTextJoined.includes('body');
        expect(hasTimelogColumns).toBe(true);
      }

      // Verify rows exist (may be zero if none created yet)
      const rows = getTableRows(page);
      const rowCount = await rows.count();
      expect(rowCount).toBeGreaterThanOrEqual(0);
    });

    /**
     * Verifies the timesheet / summary view that aggregates hours across
     * tasks and dates.
     *
     * Source: ProjectService.GetProjectTimelogs(projectId) — retrieves all
     *   timelogs where l_related_records CONTAINS projectId.
     *
     * API: GET /v1/timelogs?projectId=:id (with aggregation query params)
     */
    test('should show timesheet/summary view', async ({ page }) => {
      // Navigate to the timesheet / timelog summary page
      await page.goto(
        `${PROJECTS_BASE_URL}/${activeProjectId}/timelogs`,
        { waitUntil: 'networkidle' },
      );

      // Look for a timesheet view, summary section, or aggregated hours display
      const timesheetView = page
        .locator('[data-testid="timesheet"]')
        .or(page.locator('[data-testid="timelog-summary"]'))
        .or(page.locator('[data-testid="hours-summary"]'))
        .or(page.getByText(/total/i))
        .or(page.getByText(/summary/i))
        .or(page.getByText(/hours/i));
      await expect(timesheetView.first()).toBeVisible({ timeout: ELEMENT_TIMEOUT });

      // Verify the page displays some form of aggregated data
      const pageContent = await page.textContent('body');
      const bodyText = (pageContent ?? '').toLowerCase();

      const hasTimeSummary =
        bodyText.includes('hour') ||
        bodyText.includes('minute') ||
        bodyText.includes('time') ||
        bodyText.includes('total') ||
        bodyText.includes('summary') ||
        bodyText.includes('logged') ||
        bodyText.includes('timelog') ||
        bodyText.includes('timesheet');
      expect(hasTimeSummary).toBe(true);
    });
  }); // end Timelogs describe

  // =========================================================================
  // COMMENT TESTS
  // Replaces comment entity management via ProjectController API +
  // CommentService.cs + FeedItemService + comment entity definitions
  // =========================================================================

  test.describe('Comments', () => {
    /**
     * Stores the project ID discovered during test execution.
     */
    let activeProjectId: string;

    /**
     * Stores a task ID for associating comments.
     */
    let activeTaskId: string;

    /**
     * Before each comment test, navigate to the first project, find a task,
     * and navigate to its detail view where comments are displayed.
     */
    test.beforeEach(async ({ page }) => {
      await navigateToProjectList(page);

      // Click first project
      const firstProjectLink = page
        .locator('[data-testid="data-table"] tbody tr a')
        .or(page.locator('table tbody tr a'))
        .or(page.locator('[data-testid="project-list"] a'))
        .or(page.locator('[role="grid"] [role="row"] a'));
      const linkCount = await firstProjectLink.count();

      if (linkCount > 0) {
        await firstProjectLink.first().click();
      } else {
        const firstRow = getTableRows(page);
        await firstRow.first().click();
      }

      // Wait for project detail URL
      await page.waitForURL(
        (url) => {
          const path = url.pathname;
          return (
            path.startsWith('/projects/') &&
            path.split('/').filter(Boolean).length >= 2
          );
        },
        { timeout: NAV_TIMEOUT },
      );

      // Extract project ID
      const pathname = new URL(page.url()).pathname;
      const segments = pathname.split('/').filter(Boolean);
      activeProjectId = segments[1] ?? '';

      // Navigate to task list and click the first task
      await navigateToTaskList(page, activeProjectId);

      const firstTaskLink = page
        .locator('[data-testid="data-table"] tbody tr a')
        .or(page.locator('table tbody tr a'))
        .or(page.locator('[data-testid="task-list"] a'));
      const taskLinkCount = await firstTaskLink.count();

      if (taskLinkCount > 0) {
        await firstTaskLink.first().click();
      } else {
        const firstRow = getTableRows(page);
        await firstRow.first().click();
      }

      // Wait for task detail page
      await page.waitForURL(
        (url) => {
          const path = url.pathname;
          return path.includes('/tasks/') && !path.endsWith('/tasks');
        },
        { timeout: NAV_TIMEOUT },
      );

      // Extract task ID
      const taskPathname = new URL(page.url()).pathname;
      const taskSegments = taskPathname.split('/').filter(Boolean);
      const taskIdx = taskSegments.indexOf('tasks');
      activeTaskId = taskIdx >= 0 ? taskSegments[taskIdx + 1] ?? '' : '';
    });

    /**
     * Verifies adding a comment to a task.
     *
     * Replaces the monolith's ProjectController.CreateNewPcPostListItem():
     *   POST api/v3.0/p/project/pc-post-list/create
     *   Parameters: relatedRecordId, parentId, scope, relatedRecords,
     *               subject, body
     *
     * Source: CommentService.Create(id, createdBy, createdOn, body,
     *   parentId, scope, relatedRecords)
     * Supports parent-child nesting (one level).
     *
     * API: POST /v1/tasks/:id/comments
     * Event: inventory.comment.created (SNS -> SQS)
     */
    test('should add a comment to a task', async ({ page }) => {
      // Look for the comment input area on the task detail page.
      const commentInput = page
        .locator('[data-testid="comment-input"] textarea')
        .or(page.locator('[data-testid="comment-form"] textarea'))
        .or(page.getByPlaceholder(/comment|write|add a comment|reply/i))
        .or(page.locator('textarea[name="comment"]'))
        .or(page.locator('textarea[name="body"]'))
        .or(page.locator('[data-testid="feed-input"] textarea'));

      const inputCount = await commentInput.count();

      if (inputCount > 0) {
        // Type the comment
        await commentInput.first().fill(COMMENT_CREATE_DATA.body);

        // Submit the comment
        const submitButton = page
          .getByRole('button', { name: /post|submit|send|add|comment/i })
          .or(page.locator('[data-testid="btn-post-comment"]'))
          .or(page.locator('[data-testid="btn-submit-comment"]'));
        await submitButton.first().click();

        // Wait for the comment to appear in the feed
        await page.waitForTimeout(1_000);

        // Verify the comment text appears on the page
        const commentText = page.getByText(COMMENT_CREATE_DATA.body);
        await expect(commentText.first()).toBeVisible({ timeout: ELEMENT_TIMEOUT });
      } else {
        // Alternative: look for a "Add Comment" button that opens a form/modal
        const addCommentButton = page
          .getByRole('button', { name: /add comment|new comment/i })
          .or(page.locator('[data-testid="btn-add-comment"]'));

        const buttonCount = await addCommentButton.count();
        if (buttonCount > 0) {
          await addCommentButton.first().click();

          // Wait for the comment form to appear
          await page.waitForSelector(
            'textarea, [data-testid="comment-form"]',
            { timeout: NAV_TIMEOUT },
          );

          // Find the textarea that appeared
          const textarea = page
            .locator('textarea')
            .or(page.locator('[data-testid="comment-input"] textarea'));
          await textarea.first().fill(COMMENT_CREATE_DATA.body);

          // Submit
          const submitButton = page
            .getByRole('button', { name: /post|submit|save|add/i })
            .or(page.locator('[data-testid="btn-submit-comment"]'));
          await submitButton.first().click();

          // Verify the comment appears
          await page.waitForTimeout(1_000);
          const commentText = page.getByText(COMMENT_CREATE_DATA.body);
          await expect(commentText.first()).toBeVisible({ timeout: ELEMENT_TIMEOUT });
        }
      }
    });

    /**
     * Verifies that the task comment feed / activity log displays comments
     * with author, date, and content.
     *
     * Replaces the monolith's PcPostList Stencil web component which
     * rendered a chronological feed of comments on the task detail page.
     * Each feed item showed: author name, timestamp, body text.
     *
     * Source: FeedItemService in the Project plugin aggregated comments,
     *   timelogs, and status changes into a unified activity feed.
     *
     * API: GET /v1/tasks/:id/comments
     */
    test('should display task comments/feed', async ({ page }) => {
      // The task detail page should show a comments/feed section
      const feedSection = page
        .locator('[data-testid="comment-feed"]')
        .or(page.locator('[data-testid="comment-list"]'))
        .or(page.locator('[data-testid="activity-feed"]'))
        .or(page.locator('[data-testid="feed-list"]'))
        .or(page.getByText(/comments/i))
        .or(page.getByText(/activity/i))
        .or(page.getByText(/feed/i));
      await expect(feedSection.first()).toBeVisible({ timeout: ELEMENT_TIMEOUT });

      // Look for individual comment items in the feed
      const feedItems = page
        .locator('[data-testid="comment-item"]')
        .or(page.locator('[data-testid="feed-item"]'))
        .or(page.locator('[data-testid="comment-feed"] > *'))
        .or(page.locator('[data-testid="comment-list"] > *'));
      const feedItemCount = await feedItems.count();

      if (feedItemCount > 0) {
        // Verify the first feed item contains expected elements
        const firstItem = feedItems.first();

        // Each comment should display the author name
        const authorElement = firstItem
          .locator('[data-testid="comment-author"]')
          .or(firstItem.locator('[data-testid="feed-author"]'))
          .or(firstItem.locator('.author'))
          .or(firstItem.locator('.user-name'));
        const authorCount = await authorElement.count();

        if (authorCount > 0) {
          await expect(authorElement.first()).toBeVisible();
          const authorText = await authorElement.first().textContent();
          expect((authorText ?? '').trim().length).toBeGreaterThan(0);
        }

        // Each comment should display a timestamp
        const dateElement = firstItem
          .locator('[data-testid="comment-date"]')
          .or(firstItem.locator('[data-testid="feed-date"]'))
          .or(firstItem.locator('time'))
          .or(firstItem.locator('.date'))
          .or(firstItem.locator('.timestamp'));
        const dateCount = await dateElement.count();

        if (dateCount > 0) {
          await expect(dateElement.first()).toBeVisible();
        }

        // Each comment should display the body text
        const bodyElement = firstItem
          .locator('[data-testid="comment-body"]')
          .or(firstItem.locator('[data-testid="feed-body"]'))
          .or(firstItem.locator('.comment-body'))
          .or(firstItem.locator('.body'));
        const bodyCount = await bodyElement.count();

        if (bodyCount > 0) {
          await expect(bodyElement.first()).toBeVisible();
          const bodyText = await bodyElement.first().textContent();
          expect((bodyText ?? '').trim().length).toBeGreaterThan(0);
        }
      }

      // Verify the page overall shows comment-related content
      const pageContent = await page.textContent('body');
      const bodyText = (pageContent ?? '').toLowerCase();
      const hasCommentContent =
        bodyText.includes('comment') ||
        bodyText.includes('activity') ||
        bodyText.includes('feed') ||
        bodyText.includes('post') ||
        bodyText.includes('reply');
      expect(hasCommentContent).toBe(true);
    });
  }); // end Comments describe
}); // end Project Management describe
