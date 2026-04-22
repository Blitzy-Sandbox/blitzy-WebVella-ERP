import { defineConfig, devices } from '@playwright/test';
import { nxE2EPreset } from '@nx/playwright/preset';

/**
 * WebVella ERP Frontend E2E Test Configuration
 *
 * Playwright configuration for end-to-end testing of the React 19 SPA frontend.
 * This replaces the monolith's server-rendered Razor Pages testing approach.
 *
 * The original monolith used Razor Pages at routes such as:
 *   - /login                                             (Authentication)
 *   - /{AppName}/{AreaName}/{NodeName}/l/{PageName}      (RecordList)
 *   - /{AppName}/{AreaName}/{NodeName}/c/{PageName}      (RecordCreate)
 *   - /{AppName}/{AreaName}/{NodeName}/r/{RecordId}/{PageName} (RecordDetails)
 *   - /{AppName}/{AreaName}/{NodeName}/m/{PageName}      (RecordManage)
 *   - /{PageName?}                                       (Home / Index)
 *
 * The new React SPA serves all routes from Vite dev server at http://localhost:5173,
 * with client-side routing via React Router 7. All API calls target the LocalStack
 * HTTP API Gateway at http://localhost:4566.
 *
 * Environment Variables:
 *   - BASE_URL:         Override the default frontend URL (default: http://localhost:5173).
 *                       Use this when testing against S3 static hosting.
 *   - VITE_API_URL:     LocalStack API Gateway endpoint (set to http://localhost:4566
 *                       before test runs so the SPA communicates with LocalStack).
 *   - AWS_ENDPOINT_URL: LocalStack endpoint for direct AWS SDK calls (http://localhost:4566).
 *   - IS_LOCAL:         Set to 'true' when targeting LocalStack.
 *   - CI:               Automatically set by CI environments. Controls headless mode,
 *                       retry count, worker parallelism, and reporter format.
 *
 * Testing Pattern (per AAP §0.8.1 & §0.8.4):
 *   1. docker compose up -d       — Start LocalStack + Step Functions Local
 *   2. npx nx e2e frontend-e2e    — Run all E2E tests against LocalStack
 *   3. docker compose down        — Tear down LocalStack
 *
 * All E2E tests execute against a real LocalStack instance — no mocked AWS SDK calls.
 *
 * Workflow Coverage (extracted from monolith Razor Pages):
 *   - Authentication:  Login (email + password → Cognito), invalid credentials, logout, auth redirects
 *   - Record CRUD:     List with pagination/sorting/filtering, create with validation, details + delete, edit
 *   - Navigation:      Sidebar navigation, breadcrumbs, app switching, node navigation
 *   - Admin Console:   Entity management, field management, role management
 *   - CRM:             Account/contact CRUD
 *   - Projects:        Task management, timelogs, comments
 *   - Notifications:   Email templates, notification preferences
 *   - File Management: Upload, download, document management
 */

// ---------------------------------------------------------------------------
// Environment Detection
// ---------------------------------------------------------------------------

/** Whether the test suite is running in a CI environment */
const isCI = !!process.env.CI;

/** Base URL for the React SPA frontend — Vite dev server or S3 static hosting */
const baseURL = process.env.BASE_URL || 'http://localhost:5173';

// ---------------------------------------------------------------------------
// Playwright Configuration
// ---------------------------------------------------------------------------

/**
 * Central Playwright E2E test runner configuration for the WebVella ERP frontend.
 *
 * Extends the Nx E2E preset with custom settings for LocalStack-backed testing,
 * multi-browser coverage, and CI-optimized reporting.
 */
const playwrightConfig = defineConfig({
  // Spread Nx E2E preset for standard monorepo conventions (testDir, outputDir,
  // fullyParallel, forbidOnly, retries, workers, reporter defaults)
  ...nxE2EPreset(__filename, { testDir: './src' }),

  // -------------------------------------------------------------------------
  // Core Configuration — overrides and extensions of Nx preset defaults
  // -------------------------------------------------------------------------

  /** Directory containing all E2E test spec files */
  testDir: './src',

  /** Glob pattern matching test files — only .spec.ts files are included */
  testMatch: '**/*.spec.ts',

  /**
   * Output directory for test artifacts (traces, screenshots, videos).
   * Follows Nx workspace convention: {workspaceRoot}/dist/.playwright/{projectPath}/test-output
   */
  outputDir: '../../dist/.playwright/apps/frontend-e2e/test-output',

  /** Run tests across all files in parallel for maximum throughput */
  fullyParallel: true,

  /** Fail CI builds if test.only() is accidentally left in source code */
  forbidOnly: isCI,

  /**
   * Retry flaky tests in CI only (2 retries).
   * Locally, tests run without retries for immediate feedback.
   */
  retries: isCI ? 2 : 0,

  /**
   * Single worker in CI for deterministic, reproducible results.
   * Locally, auto-detect optimal worker count based on CPU cores.
   */
  workers: isCI ? 1 : undefined,

  /**
   * Reporter configuration:
   * - CI: HTML report (non-interactive) + JUnit XML for CI pipeline integration
   * - Local: Interactive HTML report that opens automatically
   */
  reporter: isCI
    ? [
        [
          'html',
          {
            open: 'never',
            outputFolder:
              '../../dist/.playwright/apps/frontend-e2e/playwright-report',
          },
        ],
        [
          'junit',
          {
            outputFile:
              '../../dist/.playwright/apps/frontend-e2e/results.xml',
          },
        ],
      ]
    : 'html',

  // -------------------------------------------------------------------------
  // Global Test Settings
  // -------------------------------------------------------------------------

  use: {
    /**
     * Base URL for all page.goto() calls and relative URL navigation.
     * Defaults to Vite dev server (http://localhost:5173).
     * Override via BASE_URL env var for S3 static hosting endpoints.
     */
    baseURL,

    /**
     * Capture Playwright trace on first retry for debugging flaky tests.
     * Traces include DOM snapshots, network logs, and action screenshots.
     */
    trace: 'on-first-retry',

    /**
     * Automatically capture a screenshot when a test fails.
     * Screenshots are stored in the outputDir for post-mortem analysis.
     */
    screenshot: 'only-on-failure',

    /**
     * Retain video recording only when tests fail.
     * Videos are invaluable for diagnosing timing-dependent failures
     * with LocalStack Lambda cold starts.
     */
    video: 'retain-on-failure',

    /**
     * Maximum time for each Playwright action (click, fill, type, select, etc.).
     * 10 seconds accommodates LocalStack Lambda cold start latency and
     * DynamoDB response times (AAP §0.8.2: DynamoDB P99 < 10ms, but
     * Lambda cold start < 1s for .NET AOT, < 3s for Node.js).
     */
    actionTimeout: 10_000,

    /**
     * Maximum time for page navigation (page.goto, page.reload, etc.).
     * 30 seconds allows for:
     *   - Vite dev server initial compilation
     *   - Lambda cold starts against LocalStack
     *   - API Gateway route resolution
     * AAP §0.8.2 mandates Frontend TTI < 2 seconds on 4G, so 30s provides
     * generous headroom for the LocalStack development environment.
     */
    navigationTimeout: 30_000,

    /**
     * Extra HTTP headers sent with every request during E2E tests.
     * The x-correlation-id header enables end-to-end request traceability
     * across all Lambda-backed microservices (AAP §0.8.5: structured JSON
     * logging with correlation-ID propagation from all Lambda functions).
     */
    extraHTTPHeaders: {
      'x-correlation-id': `e2e-${Date.now()}-${Math.random().toString(36).substring(2, 9)}`,
    },
  },

  // -------------------------------------------------------------------------
  // Browser Project Configurations
  // -------------------------------------------------------------------------

  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
    // Firefox and WebKit are disabled in CI/LocalStack environments where
    // only Chromium is installed via `npx playwright install chromium`.
    // Uncomment below when all browser binaries are available.
    // {
    //   name: 'firefox',
    //   use: { ...devices['Desktop Firefox'] },
    // },
    // {
    //   name: 'webkit',
    //   use: { ...devices['Desktop Safari'] },
    // },
    // {
    //   name: 'mobile-chrome',
    //   use: { ...devices['Pixel 5'] },
    // },
  ],

  // -------------------------------------------------------------------------
  // Web Server Configuration
  // -------------------------------------------------------------------------

  /**
   * Automatically start the Vite dev server before running tests.
   * Uses Nx to serve the frontend app, ensuring correct project resolution
   * and dependency awareness within the monorepo.
   */
  webServer: [
    {
      /**
       * Start the E2E mock API server (handles Cognito + API Gateway mocks).
       * Required because LocalStack Community Edition does not include
       * API Gateway v2 or Cognito services. The mock server provides all
       * necessary backend responses for E2E test execution.
       */
      command: 'node ../../tools/scripts/e2e-mock-server.mjs',
      url: 'http://localhost:3456',
      reuseExistingServer: true,
      timeout: 10_000,
      stdout: 'pipe',
      stderr: 'pipe',
      env: { MOCK_PORT: '3456' },
    },
    {
      /** Start the frontend Vite dev server with proxy targeting the mock API */
      command: `cd ../frontend && E2E_MOCK_PORT=3456 VITE_API_URL=/api VITE_COGNITO_ENDPOINT=/aws VITE_IS_LOCAL=true VITE_COGNITO_CLIENT_ID=${process.env.VITE_COGNITO_CLIENT_ID || 'mock-client-id'} VITE_API_GATEWAY_ID=${process.env.VITE_API_GATEWAY_ID || 'c7c5a2ed'} npx vite --port 5173`,

      /** URL to poll until the server is ready before running tests */
      url: 'http://localhost:5173',

      /**
       * Reuse an existing dev server when running locally for faster iteration.
       * In CI, always start a fresh server for reproducibility.
       */
      reuseExistingServer: true,

      /**
       * Maximum time to wait for the dev server to start (120 seconds).
       * This covers:
       *   - Vite initial dependency optimization / pre-bundling
       *   - TypeScript compilation via @vitejs/plugin-react
       *   - HMR warmup and module graph construction
       * AAP §0.8.2 targets Vite production build < 30 seconds;
       * dev server startup is typically faster but 120s provides headroom.
       */
      timeout: 120_000,

      /** Capture server stdout for debugging startup issues */
      stdout: 'pipe',

      /** Capture server stderr for error diagnostics */
      stderr: 'pipe',
    },
  ],
});

export default playwrightConfig;
