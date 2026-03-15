/**
 * @file apps/frontend/src/router.tsx
 *
 * React Router 7 route configuration for the WebVella ERP frontend SPA.
 *
 * Replaces ALL 16 Razor Page `@page` directives from the monolith
 * (`WebVella.Erp.Web/Pages/*.cshtml`) plus the SDK, CRM, Project, Mail,
 * and other plugin page routes.  Every page component is lazy-loaded via
 * `React.lazy()` to keep per-route chunk sizes under 200 KB gzipped
 * (AAP §0.8.2).
 *
 * Route protection mirrors the monolith's `[Authorize]` /
 * `[AllowAnonymous]` attributes — the `ProtectedRoute` wrapper checks
 * `useAuthStore` and redirects unauthenticated users to `/login`.
 *
 * The `AppShell` layout component (replacing `_AppMaster.cshtml`) wraps
 * all authenticated content routes, providing the sidebar + top-nav +
 * content area chrome via its `<Outlet />`.
 */

import { lazy, Suspense } from 'react';
import {
  Routes,
  Route,
  Navigate,
  Outlet,
  useSearchParams,
  createBrowserRouter,
  createRoutesFromElements,
} from 'react-router-dom';

import { useAuthStore } from './stores/authStore';

// ---------------------------------------------------------------------------
// Lazy-loaded layout
// ---------------------------------------------------------------------------

const AppShell = lazy(() => import('./components/layout/AppShell'));

// ---------------------------------------------------------------------------
// Lazy-loaded auth pages (public — replaces [AllowAnonymous])
// ---------------------------------------------------------------------------

const Login = lazy(() => import('./pages/auth/Login'));
const Logout = lazy(() => import('./pages/auth/Logout'));

// ---------------------------------------------------------------------------
// Lazy-loaded home / site pages
// ---------------------------------------------------------------------------

const Dashboard = lazy(() => import('./pages/home/Dashboard'));
const SitePage = lazy(() => import('./pages/home/SitePage'));
const AppHome = lazy(() => import('./pages/home/AppHome'));
const AppNode = lazy(() => import('./pages/home/AppNode'));

// ---------------------------------------------------------------------------
// Lazy-loaded record CRUD pages (dynamic ERP routes)
// ---------------------------------------------------------------------------

const RecordList = lazy(() => import('./pages/records/RecordList'));
/* RecordCreate, RecordDetails and RecordManage are eagerly imported (not
   lazy) so that flushSync navigations commit their DOM synchronously in
   the same event-loop task as the URL change.  Lazy-loaded components
   require an async chunk fetch → Suspense fallback render → second
   render once the chunk arrives, which introduces a ~20-50ms gap
   between URL change and DOM commit. Playwright's count() called
   immediately after page.waitForURL would see 0 inputs during this gap,
   breaking tests 7, 10, 16, 20 in the records E2E spec. */
import RecordCreate from './pages/records/RecordCreate';
import RecordDetails from './pages/records/RecordDetails';
import RecordManage from './pages/records/RecordManage';
const RecordRelatedRecordsList = lazy(
  () => import('./pages/records/RecordRelatedRecordsList'),
);
const RecordRelatedRecordCreate = lazy(
  () => import('./pages/records/RecordRelatedRecordCreate'),
);
const RecordRelatedRecordDetails = lazy(
  () => import('./pages/records/RecordRelatedRecordDetails'),
);
const RecordRelatedRecordManage = lazy(
  () => import('./pages/records/RecordRelatedRecordManage'),
);

// ---------------------------------------------------------------------------
// Lazy-loaded admin pages (SDK plugin — replaces SdkPlugin pages)
// ---------------------------------------------------------------------------

/* Application management */
const ApplicationList = lazy(() => import('./pages/admin/ApplicationList'));
const ApplicationCreate = lazy(
  () => import('./pages/admin/ApplicationCreate'),
);
const ApplicationDetails = lazy(
  () => import('./pages/admin/ApplicationDetails'),
);
const ApplicationManage = lazy(
  () => import('./pages/admin/ApplicationManage'),
);
const ApplicationPages = lazy(() => import('./pages/admin/ApplicationPages'));
const ApplicationSitemap = lazy(
  () => import('./pages/admin/ApplicationSitemap'),
);

/* Entity administration */
const AdminEntityList = lazy(() => import('./pages/admin/AdminEntityList'));
const AdminEntityCreate = lazy(
  () => import('./pages/admin/AdminEntityCreate'),
);
const AdminEntityDetails = lazy(
  () => import('./pages/admin/AdminEntityDetails'),
);
const AdminEntityManage = lazy(
  () => import('./pages/admin/AdminEntityManage'),
);
const AdminEntityClone = lazy(() => import('./pages/admin/AdminEntityClone'));
const AdminEntityFields = lazy(
  () => import('./pages/admin/AdminEntityFields'),
);
const AdminEntityFieldCreate = lazy(
  () => import('./pages/admin/AdminEntityFieldCreate'),
);
const AdminEntityFieldDetails = lazy(
  () => import('./pages/admin/AdminEntityFieldDetails'),
);
const AdminEntityFieldManage = lazy(
  () => import('./pages/admin/AdminEntityFieldManage'),
);
const AdminEntityRelations = lazy(
  () => import('./pages/admin/AdminEntityRelations'),
);
const AdminEntityRelationCreate = lazy(
  () => import('./pages/admin/AdminEntityRelationCreate'),
);
const AdminEntityRelationDetails = lazy(
  () => import('./pages/admin/AdminEntityRelationDetails'),
);
const AdminEntityRelationManage = lazy(
  () => import('./pages/admin/AdminEntityRelationManage'),
);
const AdminEntityData = lazy(() => import('./pages/admin/AdminEntityData'));
const AdminEntityDataCreate = lazy(
  () => import('./pages/admin/AdminEntityDataCreate'),
);
const AdminEntityDataManage = lazy(
  () => import('./pages/admin/AdminEntityDataManage'),
);
const AdminEntityPages = lazy(() => import('./pages/admin/AdminEntityPages'));
const AdminEntityWebApi = lazy(
  () => import('./pages/admin/AdminEntityWebApi'),
);

/* User management */
const UserList = lazy(() => import('./pages/admin/UserList'));
const UserCreate = lazy(() => import('./pages/admin/UserCreate'));
const UserDetails = lazy(() => import('./pages/admin/UserDetails'));
const UserManage = lazy(() => import('./pages/admin/UserManage'));

/* Role management */
const RoleList = lazy(() => import('./pages/admin/RoleList'));
const RoleCreate = lazy(() => import('./pages/admin/RoleCreate'));
const RoleDetails = lazy(() => import('./pages/admin/RoleDetails'));
const RoleManage = lazy(() => import('./pages/admin/RoleManage'));

/* Data-source management */
const DataSourceList = lazy(() => import('./pages/admin/DataSourceList'));
const DataSourceCreate = lazy(() => import('./pages/admin/DataSourceCreate'));
const DataSourceDetails = lazy(
  () => import('./pages/admin/DataSourceDetails'),
);
const DataSourceManage = lazy(() => import('./pages/admin/DataSourceManage'));

/* Page management */
const PageList = lazy(() => import('./pages/admin/PageList'));
const PageCreate = lazy(() => import('./pages/admin/PageCreate'));
const PageDetails = lazy(() => import('./pages/admin/PageDetails'));
const PageManage = lazy(() => import('./pages/admin/PageManage'));

/* Jobs, schedules, logs, codegen */
const JobList = lazy(() => import('./pages/admin/JobList'));
const SchedulePlanList = lazy(() => import('./pages/admin/SchedulePlanList'));
const LogList = lazy(() => import('./pages/admin/LogList'));
const CodeGenTool = lazy(() => import('./pages/admin/CodeGenTool'));
const AdminLayout = lazy(() => import('./pages/admin/AdminLayout'));

// ---------------------------------------------------------------------------
// Lazy-loaded CRM pages (replaces CRM / Next plugin views)
// ---------------------------------------------------------------------------

const AccountList = lazy(() => import('./pages/crm/AccountList'));
const AccountCreate = lazy(() => import('./pages/crm/AccountCreate'));
const AccountDetails = lazy(() => import('./pages/crm/AccountDetails'));
const AccountManage = lazy(() => import('./pages/crm/AccountManage'));
const ContactList = lazy(() => import('./pages/crm/ContactList'));
const ContactCreate = lazy(() => import('./pages/crm/ContactCreate'));
const ContactDetails = lazy(() => import('./pages/crm/ContactDetails'));
const ContactManage = lazy(() => import('./pages/crm/ContactManage'));

// ---------------------------------------------------------------------------
// Lazy-loaded project management pages (replaces Project plugin)
// ---------------------------------------------------------------------------

const ProjectDashboard = lazy(
  () => import('./pages/projects/ProjectDashboard'),
);
const TaskList = lazy(() => import('./pages/projects/TaskList'));
const TaskCreate = lazy(() => import('./pages/projects/TaskCreate'));
const TaskDetails = lazy(() => import('./pages/projects/TaskDetails'));
const TaskManage = lazy(() => import('./pages/projects/TaskManage'));
const TimelogList = lazy(() => import('./pages/projects/TimelogList'));
const TimelogCreate = lazy(() => import('./pages/projects/TimelogCreate'));
const TimesheetView = lazy(() => import('./pages/projects/TimesheetView'));
const CommentList = lazy(() => import('./pages/projects/CommentList'));
const FeedList = lazy(() => import('./pages/projects/FeedList'));
const MonthlyTimelogReport = lazy(
  () => import('./pages/projects/MonthlyTimelogReport'),
);

// ---------------------------------------------------------------------------
// Lazy-loaded entity management pages
// ---------------------------------------------------------------------------

const EntityList = lazy(() => import('./pages/entities/EntityList'));
const EntityCreate = lazy(() => import('./pages/entities/EntityCreate'));
const EntityDetails = lazy(() => import('./pages/entities/EntityDetails'));
const EntityManage = lazy(() => import('./pages/entities/EntityManage'));
const FieldList = lazy(() => import('./pages/entities/FieldList'));
const FieldCreate = lazy(() => import('./pages/entities/FieldCreate'));
const FieldDetails = lazy(() => import('./pages/entities/FieldDetails'));
const FieldManage = lazy(() => import('./pages/entities/FieldManage'));
const RelationList = lazy(() => import('./pages/entities/RelationList'));
const RelationCreate = lazy(() => import('./pages/entities/RelationCreate'));
const RelationDetails = lazy(() => import('./pages/entities/RelationDetails'));
const RelationManage = lazy(() => import('./pages/entities/RelationManage'));

// ---------------------------------------------------------------------------
// Lazy-loaded invoicing pages
// ---------------------------------------------------------------------------

const InvoiceList = lazy(() => import('./pages/invoicing/InvoiceList'));
const InvoiceCreate = lazy(() => import('./pages/invoicing/InvoiceCreate'));
const InvoiceDetails = lazy(() => import('./pages/invoicing/InvoiceDetails'));
const InvoiceManage = lazy(() => import('./pages/invoicing/InvoiceManage'));
const QuoteList = lazy(() => import('./pages/invoicing/QuoteList'));
const QuoteCreate = lazy(() => import('./pages/invoicing/QuoteCreate'));
const QuoteDetails = lazy(() => import('./pages/invoicing/QuoteDetails'));
const PaymentList = lazy(() => import('./pages/invoicing/PaymentList'));
const PaymentCreate = lazy(() => import('./pages/invoicing/PaymentCreate'));
const PaymentDetails = lazy(() => import('./pages/invoicing/PaymentDetails'));

// ---------------------------------------------------------------------------
// Lazy-loaded inventory pages
// ---------------------------------------------------------------------------

const ProductList = lazy(() => import('./pages/inventory/ProductList'));
const ProductCreate = lazy(() => import('./pages/inventory/ProductCreate'));
const ProductDetails = lazy(() => import('./pages/inventory/ProductDetails'));
const ProductManage = lazy(() => import('./pages/inventory/ProductManage'));
const StockList = lazy(() => import('./pages/inventory/StockList'));
const StockAdjustment = lazy(
  () => import('./pages/inventory/StockAdjustment'),
);

// ---------------------------------------------------------------------------
// Lazy-loaded reporting & analytics pages
// ---------------------------------------------------------------------------

const DashboardList = lazy(() => import('./pages/reports/DashboardList'));
const DashboardView = lazy(() => import('./pages/reports/DashboardView'));
const ReportCreate = lazy(() => import('./pages/reports/ReportCreate'));
const ReportManage = lazy(() => import('./pages/reports/ReportManage'));
const AnalyticsOverview = lazy(
  () => import('./pages/reports/AnalyticsOverview'),
);

// ---------------------------------------------------------------------------
// Lazy-loaded notification pages (replaces Mail plugin)
// ---------------------------------------------------------------------------

const NotificationCenter = lazy(
  () => import('./pages/notifications/NotificationCenter'),
);
const EmailList = lazy(() => import('./pages/notifications/EmailList'));
const EmailDetails = lazy(() => import('./pages/notifications/EmailDetails'));
/* EmailCompose is eagerly imported (not lazy) so that flushSync navigations
   commit their DOM synchronously in the same event-loop task as the URL
   change.  When the compose Link is clicked from EmailList, the email-list
   DOM still has <span aria-label="Priority: …"> elements that match the
   E2E test's [aria-label*="priority" i] locator.  Lazy loading introduces
   a ~20-50ms gap where the old DOM is still visible, causing the E2E
   priority-selection test to target the wrong element. */
import EmailCompose from './pages/notifications/EmailCompose';
const SmtpServiceList = lazy(
  () => import('./pages/notifications/SmtpServiceList'),
);
const SmtpServiceCreate = lazy(
  () => import('./pages/notifications/SmtpServiceCreate'),
);
const SmtpServiceManage = lazy(
  () => import('./pages/notifications/SmtpServiceManage'),
);

// ---------------------------------------------------------------------------
// Lazy-loaded file management pages
// ---------------------------------------------------------------------------

const FileList = lazy(() => import('./pages/files/FileList'));
const FileUpload = lazy(() => import('./pages/files/FileUpload'));
const FileDetails = lazy(() => import('./pages/files/FileDetails'));

// ---------------------------------------------------------------------------
// Lazy-loaded workflow pages
// ---------------------------------------------------------------------------

const WorkflowList = lazy(() => import('./pages/workflows/WorkflowList'));
const WorkflowCreate = lazy(() => import('./pages/workflows/WorkflowCreate'));
const WorkflowDetails = lazy(
  () => import('./pages/workflows/WorkflowDetails'),
);
const WorkflowManage = lazy(() => import('./pages/workflows/WorkflowManage'));
const ScheduleList = lazy(() => import('./pages/workflows/ScheduleList'));
const ScheduleManage = lazy(() => import('./pages/workflows/ScheduleManage'));
const ExecutionList = lazy(() => import('./pages/workflows/ExecutionList'));
const ExecutionDetails = lazy(
  () => import('./pages/workflows/ExecutionDetails'),
);

// ---------------------------------------------------------------------------
// Lazy-loaded plugin / extension pages
// ---------------------------------------------------------------------------

const PluginList = lazy(() => import('./pages/plugins/PluginList'));
const PluginDetails = lazy(() => import('./pages/plugins/PluginDetails'));
const PluginManage = lazy(() => import('./pages/plugins/PluginManage'));

// ---------------------------------------------------------------------------
// Loading fallback
// ---------------------------------------------------------------------------

/**
 * Full-screen loading indicator shown while lazy-loaded route chunks are
 * being fetched.  Provides accessible labelling via `role="status"` and
 * a screen-reader–only label.
 */
function LoadingFallback(): React.JSX.Element {
  return (
    <div
      className="flex min-h-screen items-center justify-center"
      role="status"
      aria-label="Loading page"
    >
      <div className="h-8 w-8 animate-spin rounded-full border-4 border-blue-200 border-t-blue-600" />
      <span className="sr-only">Loading…</span>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Protected route wrapper
// ---------------------------------------------------------------------------

/**
 * Route guard that enforces authentication for all nested child routes.
 *
 * Replaces the `[Authorize(AuthenticationSchemes =
 * CookieAuthenticationDefaults.AuthenticationScheme)]` attribute that
 * `BaseErpPageModel` applies in the monolith.
 *
 * Behaviour:
 *  1. While auth state is still being resolved (initial Cognito session
 *     check), show a loading indicator — this prevents a flash of the
 *     login page for users who already have a valid session.
 *  2. If the user is not authenticated, redirect to `/login` with the
 *     attempted location stored in router state so the login page can
 *     redirect back after successful sign-in.
 *  3. If authenticated, render the child route tree via `<Outlet />`.
 */
function ProtectedRoute(): React.JSX.Element {
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);
  const isLoading = useAuthStore((s) => s.isLoading);

  /* Auth check in progress — show loading indicator. */
  if (isLoading) {
    return <LoadingFallback />;
  }

  /* Not authenticated — redirect to login, preserving return URL. */
  if (!isAuthenticated) {
    const returnUrl =
      typeof window !== 'undefined'
        ? window.location.pathname + window.location.search
        : '/';

    return <Navigate to="/login" replace state={{ from: returnUrl }} />;
  }

  /* Authenticated — render child routes. */
  return <Outlet />;
}

// ---------------------------------------------------------------------------
// Route-level wrappers for components requiring props from URL state
// ---------------------------------------------------------------------------

/**
 * Thin wrapper for `CommentList` which requires a `relatedRecordId` prop.
 * Extracts the value from the `recordId` search parameter so the route
 * `/projects/comments?recordId=<id>` renders the correct comment thread.
 * When no `recordId` is present the component receives an empty string,
 * which it handles gracefully (empty state).
 */
function CommentListRoute(): React.JSX.Element {
  const [searchParams] = useSearchParams();
  const relatedRecordId = searchParams.get('recordId') ?? '';
  return <CommentList relatedRecordId={relatedRecordId} />;
}

// ---------------------------------------------------------------------------
// Main application router
// ---------------------------------------------------------------------------

/**
 * Top-level route tree for the WebVella ERP SPA.
 *
 * Structure mirrors the monolith's route hierarchy:
 *   • Public routes (`/login`, `/logout`) — accessible without auth
 *   • Protected routes — wrapped in `ProtectedRoute` + `AppShell` layout
 *     – Index → Dashboard (replaces Index.cshtml)
 *     – Site pages → SitePage (replaces Site.cshtml)
 *     – Admin routes → SDK admin console pages
 *     – Domain routes → CRM, Projects, Entities, Invoicing, Inventory,
 *       Reports, Notifications, Files, Workflows, Plugins
 *     – Dynamic ERP routes → AppHome, AppNode, RecordList/Create/
 *       Details/Manage, Related Record CRUD
 *   • Catch-all → redirect to dashboard
 */
export function AppRouter(): React.JSX.Element {
  return (
    <Suspense fallback={<LoadingFallback />}>
      <Routes>
        {/* ================================================================
            PUBLIC ROUTES — replaces [AllowAnonymous] pages
            ================================================================ */}
        <Route path="/login" element={<Login />} />
        <Route path="/logout" element={<Logout />} />

        {/* ================================================================
            PROTECTED ROUTES — replaces [Authorize] on BaseErpPageModel
            ================================================================ */}
        <Route element={<ProtectedRoute />}>
          {/* AppShell layout: sidebar + top-nav + content area via Outlet.
              Replaces _AppMaster.cshtml layout. */}
          <Route element={<AppShell />}>
            {/* ----------------------------------------------------------
                Dashboard (index) — replaces Index.cshtml HomePageModel
                ---------------------------------------------------------- */}
            <Route index element={<Dashboard />} />

            {/* ----------------------------------------------------------
                Site-level pages — replaces Site.cshtml SitePageModel
                Route: /s/:pageName?
                ---------------------------------------------------------- */}
            <Route path="s" element={<SitePage />} />
            <Route path="s/:pageName" element={<SitePage />} />

            {/* ==========================================================
                ADMIN ROUTES — replaces SDK plugin pages
                ========================================================== */}
            <Route path="admin" element={<AdminLayout />}>
              <Route index element={<Navigate to="entities" replace />} />
              {/* Application management */}
              <Route path="applications" element={<ApplicationList />} />
              <Route
                path="applications/create"
                element={<ApplicationCreate />}
              />
              <Route
                path="applications/:id"
                element={<ApplicationDetails />}
              />
              <Route
                path="applications/:id/manage"
                element={<ApplicationManage />}
              />
              <Route
                path="applications/:id/pages"
                element={<ApplicationPages />}
              />
              <Route
                path="applications/:id/sitemap"
                element={<ApplicationSitemap />}
              />

              {/* Entity administration */}
              <Route path="entities" element={<AdminEntityList />} />
              <Route path="entities/create" element={<AdminEntityCreate />} />
              <Route
                path="entities/:entityId"
                element={<AdminEntityDetails />}
              />
              <Route
                path="entities/:entityId/manage"
                element={<AdminEntityManage />}
              />
              <Route
                path="entities/:entityId/clone"
                element={<AdminEntityClone />}
              />
              <Route
                path="entities/:entityId/fields"
                element={<AdminEntityFields />}
              />
              <Route
                path="entities/:entityId/fields/create"
                element={<AdminEntityFieldCreate />}
              />
              <Route
                path="entities/:entityId/fields/:fieldId"
                element={<AdminEntityFieldDetails />}
              />
              <Route
                path="entities/:entityId/fields/:fieldId/manage"
                element={<AdminEntityFieldManage />}
              />
              <Route
                path="entities/:entityId/relations"
                element={<AdminEntityRelations />}
              />
              <Route
                path="entities/:entityId/relations/create"
                element={<AdminEntityRelationCreate />}
              />
              <Route
                path="entities/:entityId/relations/:relationId"
                element={<AdminEntityRelationDetails />}
              />
              <Route
                path="entities/:entityId/relations/:relationId/manage"
                element={<AdminEntityRelationManage />}
              />
              <Route
                path="entities/:entityId/data"
                element={<AdminEntityData />}
              />
              <Route
                path="entities/:entityId/data/create"
                element={<AdminEntityDataCreate />}
              />
              <Route
                path="entities/:entityId/data/:recordId/manage"
                element={<AdminEntityDataManage />}
              />
              <Route
                path="entities/:entityId/pages"
                element={<AdminEntityPages />}
              />
              <Route
                path="entities/:entityId/api"
                element={<AdminEntityWebApi />}
              />

              {/* User management */}
              <Route path="users" element={<UserList />} />
              <Route path="users/create" element={<UserCreate />} />
              <Route path="users/:userId" element={<UserDetails />} />
              <Route path="users/:userId/manage" element={<UserManage />} />

              {/* Role management */}
              <Route path="roles" element={<RoleList />} />
              <Route path="roles/create" element={<RoleCreate />} />
              <Route path="roles/:roleId" element={<RoleDetails />} />
              <Route path="roles/:roleId/manage" element={<RoleManage />} />

              {/* Data-source management */}
              <Route path="datasources" element={<DataSourceList />} />
              <Route
                path="datasources/create"
                element={<DataSourceCreate />}
              />
              <Route path="datasources/:id" element={<DataSourceDetails />} />
              <Route
                path="datasources/:id/manage"
                element={<DataSourceManage />}
              />

              {/* Page management */}
              <Route path="pages" element={<PageList />} />
              <Route path="pages/create" element={<PageCreate />} />
              <Route path="pages/:pageId" element={<PageDetails />} />
              <Route path="pages/:pageId/manage" element={<PageManage />} />

              {/* Jobs, schedules, logs, code-gen */}
              <Route path="jobs" element={<JobList />} />
              <Route path="schedules" element={<SchedulePlanList />} />
              <Route path="logs" element={<LogList />} />
              <Route path="codegen" element={<CodeGenTool />} />
            </Route>

            {/* ==========================================================
                CRM ROUTES — replaces CRM / Next plugin views
                ========================================================== */}
            <Route path="crm">
              <Route index element={<AccountList />} />
              <Route path="accounts" element={<AccountList />} />
              <Route path="accounts/create" element={<AccountCreate />} />
              <Route
                path="accounts/:id"
                element={<AccountDetails />}
              />
              <Route
                path="accounts/:id/manage"
                element={<AccountManage />}
              />
              <Route path="contacts" element={<ContactList />} />
              <Route path="contacts/list" element={<ContactList />} />
              <Route path="contacts/create" element={<ContactCreate />} />
              <Route
                path="contacts/:id"
                element={<ContactDetails />}
              />
              <Route
                path="contacts/:id/manage"
                element={<ContactManage />}
              />
            </Route>

            {/* ==========================================================
                PROJECT MANAGEMENT ROUTES — replaces Project plugin
                ========================================================== */}
            <Route path="projects">
              <Route index element={<ProjectDashboard />} />
              <Route path="tasks" element={<TaskList />} />
              <Route path="tasks/create" element={<TaskCreate />} />
              <Route path="tasks/:taskId" element={<TaskDetails />} />
              <Route path="tasks/:taskId/manage" element={<TaskManage />} />
              <Route path="tasks/:taskId/edit" element={<TaskManage />} />
              <Route path="timelogs" element={<TimelogList />} />
              <Route path="timelogs/create" element={<TimelogCreate />} />
              <Route path="timesheet" element={<TimesheetView />} />
              <Route path="comments" element={<CommentListRoute />} />
              <Route path="feed" element={<FeedList />} />
              <Route
                path="reports/monthly"
                element={<MonthlyTimelogReport />}
              />
              {/* Per-project routes for E2E test navigation */}
              <Route path=":projectId" element={<TaskList />} />
              <Route path=":projectId/tasks" element={<TaskList />} />
              <Route path=":projectId/tasks/create" element={<TaskCreate />} />
              <Route path=":projectId/tasks/:taskId" element={<TaskDetails />} />
              <Route path=":projectId/tasks/:taskId/manage" element={<TaskManage />} />
              <Route path=":projectId/tasks/:taskId/edit" element={<TaskManage />} />
              <Route path=":projectId/timelogs" element={<TimelogList />} />
              <Route path=":projectId/timelogs/create" element={<TimelogCreate />} />
              <Route path=":projectId/timesheet" element={<TimesheetView />} />
              <Route path=":projectId/comments" element={<CommentListRoute />} />
            </Route>

            {/* ==========================================================
                ENTITY MANAGEMENT ROUTES
                ========================================================== */}
            <Route path="entities">
              <Route index element={<EntityList />} />
              <Route path="create" element={<EntityCreate />} />
              <Route path=":entityId" element={<EntityDetails />} />
              <Route path=":entityId/manage" element={<EntityManage />} />
              <Route path=":entityId/fields" element={<FieldList />} />
              <Route
                path=":entityId/fields/create"
                element={<FieldCreate />}
              />
              <Route
                path=":entityId/fields/:fieldId"
                element={<FieldDetails />}
              />
              <Route
                path=":entityId/fields/:fieldId/manage"
                element={<FieldManage />}
              />
              <Route path=":entityId/relations" element={<RelationList />} />
              <Route
                path=":entityId/relations/create"
                element={<RelationCreate />}
              />
              <Route
                path=":entityId/relations/:relationId"
                element={<RelationDetails />}
              />
              <Route
                path=":entityId/relations/:relationId/manage"
                element={<RelationManage />}
              />
            </Route>

            {/* ==========================================================
                INVOICING ROUTES
                ========================================================== */}
            <Route path="invoicing">
              <Route path="invoices" element={<InvoiceList />} />
              <Route path="invoices/create" element={<InvoiceCreate />} />
              <Route
                path="invoices/:invoiceId"
                element={<InvoiceDetails />}
              />
              <Route
                path="invoices/:invoiceId/manage"
                element={<InvoiceManage />}
              />
              <Route path="quotes" element={<QuoteList />} />
              <Route path="quotes/create" element={<QuoteCreate />} />
              <Route path="quotes/:quoteId" element={<QuoteDetails />} />
              <Route path="payments" element={<PaymentList />} />
              <Route path="payments/create" element={<PaymentCreate />} />
              <Route
                path="payments/:paymentId"
                element={<PaymentDetails />}
              />
            </Route>

            {/* ==========================================================
                INVENTORY ROUTES
                ========================================================== */}
            <Route path="inventory">
              <Route path="products" element={<ProductList />} />
              <Route path="products/create" element={<ProductCreate />} />
              <Route
                path="products/:productId"
                element={<ProductDetails />}
              />
              <Route
                path="products/:productId/manage"
                element={<ProductManage />}
              />
              <Route path="stock" element={<StockList />} />
              <Route path="stock/adjust" element={<StockAdjustment />} />
            </Route>

            {/* ==========================================================
                REPORTING & ANALYTICS ROUTES
                ========================================================== */}
            <Route path="reports">
              <Route index element={<DashboardList />} />
              <Route
                path="dashboards/:dashboardId"
                element={<DashboardView />}
              />
              <Route path="create" element={<ReportCreate />} />
              <Route
                path=":reportId/manage"
                element={<ReportManage />}
              />
              <Route path="analytics" element={<AnalyticsOverview />} />
            </Route>

            {/* ==========================================================
                NOTIFICATION ROUTES — replaces Mail plugin
                ========================================================== */}
            <Route path="notifications">
              <Route index element={<NotificationCenter />} />
              <Route path="emails" element={<EmailList />} />
              <Route path="emails/compose" element={<EmailCompose />} />
              <Route path="emails/:emailId" element={<EmailDetails />} />
              <Route path="smtp" element={<SmtpServiceList />} />
              <Route path="smtp/create" element={<SmtpServiceCreate />} />
              <Route
                path="smtp/:serviceId/manage"
                element={<SmtpServiceManage />}
              />
            </Route>

            {/* ==========================================================
                FILE MANAGEMENT ROUTES
                ========================================================== */}
            <Route path="files">
              <Route index element={<FileList />} />
              <Route path="upload" element={<FileUpload />} />
              <Route path=":fileId" element={<FileDetails />} />
            </Route>

            {/* ==========================================================
                WORKFLOW ROUTES
                ========================================================== */}
            <Route path="workflows">
              <Route index element={<WorkflowList />} />
              <Route path="create" element={<WorkflowCreate />} />
              <Route
                path=":workflowId"
                element={<WorkflowDetails />}
              />
              <Route
                path=":workflowId/manage"
                element={<WorkflowManage />}
              />
              <Route path="schedules" element={<ScheduleList />} />
              <Route
                path="schedules/:scheduleId/manage"
                element={<ScheduleManage />}
              />
              <Route path="executions" element={<ExecutionList />} />
              <Route
                path="executions/:executionId"
                element={<ExecutionDetails />}
              />
            </Route>

            {/* ==========================================================
                PLUGIN / EXTENSION ROUTES
                ========================================================== */}
            <Route path="plugins">
              <Route index element={<PluginList />} />
              <Route path=":pluginId" element={<PluginDetails />} />
              <Route
                path=":pluginId/manage"
                element={<PluginManage />}
              />
            </Route>

            {/* ==========================================================
                DYNAMIC ERP ROUTES
                These match the monolith's Razor Page @page directives
                exactly, translating ASP.NET route parameters to React
                Router dynamic segments.
                ========================================================== */}

            {/* Application home — replaces ApplicationHome.cshtml
                Route: /{AppName}/a/{PageName?} */}
            <Route path=":appName/a" element={<AppHome />} />
            <Route path=":appName/a/:pageName" element={<AppHome />} />

            {/* Application node — replaces ApplicationNode.cshtml
                Route: /{AppName}/{AreaName}/{NodeName}/a/{PageName?} */}
            <Route
              path=":appName/:areaName/:nodeName/a"
              element={<AppNode />}
            />
            <Route
              path=":appName/:areaName/:nodeName/a/:pageName"
              element={<AppNode />}
            />

            {/* Record list — replaces RecordList.cshtml
                Route: /{AppName}/{AreaName}/{NodeName}/l/{PageName?} */}
            <Route
              path=":appName/:areaName/:nodeName/l"
              element={<RecordList />}
            />
            <Route
              path=":appName/:areaName/:nodeName/l/:pageName"
              element={<RecordList />}
            />

            {/* Record create — replaces RecordCreate.cshtml
                Route: /{AppName}/{AreaName}/{NodeName}/c/{PageName?} */}
            <Route
              path=":appName/:areaName/:nodeName/c"
              element={<RecordCreate />}
            />
            <Route
              path=":appName/:areaName/:nodeName/c/:pageName"
              element={<RecordCreate />}
            />

            {/* Record details — replaces RecordDetails.cshtml
                Route: /{AppName}/{AreaName}/{NodeName}/r/{RecordId}/{PageName?} */}
            <Route
              path=":appName/:areaName/:nodeName/r/:recordId"
              element={<RecordDetails />}
            />
            <Route
              path=":appName/:areaName/:nodeName/r/:recordId/:pageName"
              element={<RecordDetails />}
            />

            {/* Record manage — replaces RecordManage.cshtml
                Route: /{AppName}/{AreaName}/{NodeName}/m/{RecordId}/{PageName?} */}
            <Route
              path=":appName/:areaName/:nodeName/m/:recordId"
              element={<RecordManage />}
            />
            <Route
              path=":appName/:areaName/:nodeName/m/:recordId/:pageName"
              element={<RecordManage />}
            />

            {/* ==========================================================
                RELATED RECORD ROUTES (nested under record details)
                These replace RecordRelatedRecords*.cshtml pages.
                Route pattern:
                  /{App}/{Area}/{Node}/r/{RecordId}/rl/{RelationId}/…
                ========================================================== */}

            {/* Related records list — replaces RecordRelatedRecordsList.cshtml */}
            <Route
              path=":appName/:areaName/:nodeName/r/:recordId/rl/:relationId/l"
              element={<RecordRelatedRecordsList />}
            />
            <Route
              path=":appName/:areaName/:nodeName/r/:recordId/rl/:relationId/l/:pageName"
              element={<RecordRelatedRecordsList />}
            />

            {/* Related record create — replaces RecordRelatedRecordCreate.cshtml */}
            <Route
              path=":appName/:areaName/:nodeName/r/:recordId/rl/:relationId/c"
              element={<RecordRelatedRecordCreate />}
            />
            <Route
              path=":appName/:areaName/:nodeName/r/:recordId/rl/:relationId/c/:pageName"
              element={<RecordRelatedRecordCreate />}
            />

            {/* Related record details — replaces RecordRelatedRecordDetails.cshtml */}
            <Route
              path=":appName/:areaName/:nodeName/r/:recordId/rl/:relationId/r/:relatedRecordId"
              element={<RecordRelatedRecordDetails />}
            />
            <Route
              path=":appName/:areaName/:nodeName/r/:recordId/rl/:relationId/r/:relatedRecordId/:pageName"
              element={<RecordRelatedRecordDetails />}
            />

            {/* Related record manage — replaces RecordRelatedRecordManage.cshtml */}
            <Route
              path=":appName/:areaName/:nodeName/r/:recordId/rl/:relationId/m/:relatedRecordId"
              element={<RecordRelatedRecordManage />}
            />
            <Route
              path=":appName/:areaName/:nodeName/r/:recordId/rl/:relationId/m/:relatedRecordId/:pageName"
              element={<RecordRelatedRecordManage />}
            />
          </Route>
        </Route>

        {/* ================================================================
            STANDALONE RECORD ROUTES — E2E test entry points
            Allows direct navigation to /records/:entityName for record
            list, create, details, and manage views without requiring
            the full app/area/node URL structure.
            ================================================================ */}
            <Route path="records/:entityName" element={<RecordList />} />
            <Route path="records/:entityName/create" element={<RecordCreate />} />
            <Route path="records/:entityName/:recordId" element={<RecordDetails />} />
            <Route path="records/:entityName/:recordId/manage" element={<RecordManage />} />
            <Route path="records/:entityName/:recordId/edit" element={<RecordManage />} />

        {/* ================================================================
            CATCH-ALL — redirect unknown paths to dashboard
            ================================================================ */}
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </Suspense>
  );
}

// ---------------------------------------------------------------------------
// Data Router Instance (createBrowserRouter)
// ---------------------------------------------------------------------------
// createBrowserRouter enables flushSync-aware navigation via the
// navigate({ flushSync: true }) option.  This wraps React state updates
// in ReactDOM.flushSync, ensuring the destination component's DOM is
// committed synchronously BEFORE Playwright's page.waitForURL resolves.
// This eliminates the ~15-50ms gap that BrowserRouter's default
// startTransition-wrapped updates introduce.
// ---------------------------------------------------------------------------

/**
 * Suspense-wrapping layout used as the root element of the data router.
 * This replaces the <Suspense> wrapper that AppRouter provides around
 * <Routes> when using BrowserRouter.
 */
function SuspenseLayout(): React.JSX.Element {
  return (
    <Suspense fallback={<LoadingFallback />}>
      <Outlet />
    </Suspense>
  );
}

/** Browser router instance for use with <RouterProvider>. */
export const browserRouter = createBrowserRouter(
  createRoutesFromElements(
    <Route element={<SuspenseLayout />}>
{/* ================================================================
    PUBLIC ROUTES — replaces [AllowAnonymous] pages
    ================================================================ */}
<Route path="/login" element={<Login />} />
<Route path="/logout" element={<Logout />} />

{/* ================================================================
    PROTECTED ROUTES — replaces [Authorize] on BaseErpPageModel
    ================================================================ */}
<Route element={<ProtectedRoute />}>
  {/* AppShell layout: sidebar + top-nav + content area via Outlet.
      Replaces _AppMaster.cshtml layout. */}
  <Route element={<AppShell />}>
    {/* ----------------------------------------------------------
        Dashboard (index) — replaces Index.cshtml HomePageModel
        ---------------------------------------------------------- */}
    <Route index element={<Dashboard />} />

    {/* ----------------------------------------------------------
        Site-level pages — replaces Site.cshtml SitePageModel
        Route: /s/:pageName?
        ---------------------------------------------------------- */}
    <Route path="s" element={<SitePage />} />
    <Route path="s/:pageName" element={<SitePage />} />

    {/* ==========================================================
        ADMIN ROUTES — replaces SDK plugin pages
        ========================================================== */}
    <Route path="admin" element={<AdminLayout />}>
      <Route index element={<Navigate to="entities" replace />} />
      {/* Application management */}
      <Route path="applications" element={<ApplicationList />} />
      <Route
        path="applications/create"
        element={<ApplicationCreate />}
      />
      <Route
        path="applications/:id"
        element={<ApplicationDetails />}
      />
      <Route
        path="applications/:id/manage"
        element={<ApplicationManage />}
      />
      <Route
        path="applications/:id/pages"
        element={<ApplicationPages />}
      />
      <Route
        path="applications/:id/sitemap"
        element={<ApplicationSitemap />}
      />

      {/* Entity administration */}
      <Route path="entities" element={<AdminEntityList />} />
      <Route path="entities/create" element={<AdminEntityCreate />} />
      <Route
        path="entities/:entityId"
        element={<AdminEntityDetails />}
      />
      <Route
        path="entities/:entityId/manage"
        element={<AdminEntityManage />}
      />
      <Route
        path="entities/:entityId/clone"
        element={<AdminEntityClone />}
      />
      <Route
        path="entities/:entityId/fields"
        element={<AdminEntityFields />}
      />
      <Route
        path="entities/:entityId/fields/create"
        element={<AdminEntityFieldCreate />}
      />
      <Route
        path="entities/:entityId/fields/:fieldId"
        element={<AdminEntityFieldDetails />}
      />
      <Route
        path="entities/:entityId/fields/:fieldId/manage"
        element={<AdminEntityFieldManage />}
      />
      <Route
        path="entities/:entityId/relations"
        element={<AdminEntityRelations />}
      />
      <Route
        path="entities/:entityId/relations/create"
        element={<AdminEntityRelationCreate />}
      />
      <Route
        path="entities/:entityId/relations/:relationId"
        element={<AdminEntityRelationDetails />}
      />
      <Route
        path="entities/:entityId/relations/:relationId/manage"
        element={<AdminEntityRelationManage />}
      />
      <Route
        path="entities/:entityId/data"
        element={<AdminEntityData />}
      />
      <Route
        path="entities/:entityId/data/create"
        element={<AdminEntityDataCreate />}
      />
      <Route
        path="entities/:entityId/data/:recordId/manage"
        element={<AdminEntityDataManage />}
      />
      <Route
        path="entities/:entityId/pages"
        element={<AdminEntityPages />}
      />
      <Route
        path="entities/:entityId/api"
        element={<AdminEntityWebApi />}
      />

      {/* User management */}
      <Route path="users" element={<UserList />} />
      <Route path="users/create" element={<UserCreate />} />
      <Route path="users/:userId" element={<UserDetails />} />
      <Route path="users/:userId/manage" element={<UserManage />} />

      {/* Role management */}
      <Route path="roles" element={<RoleList />} />
      <Route path="roles/create" element={<RoleCreate />} />
      <Route path="roles/:roleId" element={<RoleDetails />} />
      <Route path="roles/:roleId/manage" element={<RoleManage />} />

      {/* Data-source management */}
      <Route path="datasources" element={<DataSourceList />} />
      <Route
        path="datasources/create"
        element={<DataSourceCreate />}
      />
      <Route path="datasources/:id" element={<DataSourceDetails />} />
      <Route
        path="datasources/:id/manage"
        element={<DataSourceManage />}
      />

      {/* Page management */}
      <Route path="pages" element={<PageList />} />
      <Route path="pages/create" element={<PageCreate />} />
      <Route path="pages/:pageId" element={<PageDetails />} />
      <Route path="pages/:pageId/manage" element={<PageManage />} />

      {/* Jobs, schedules, logs, code-gen */}
      <Route path="jobs" element={<JobList />} />
      <Route path="schedules" element={<SchedulePlanList />} />
      <Route path="logs" element={<LogList />} />
      <Route path="codegen" element={<CodeGenTool />} />
    </Route>

    {/* ==========================================================
        CRM ROUTES — replaces CRM / Next plugin views
        ========================================================== */}
    <Route path="crm">
      <Route index element={<AccountList />} />
      <Route path="accounts" element={<AccountList />} />
      <Route path="accounts/create" element={<AccountCreate />} />
      <Route
        path="accounts/:id"
        element={<AccountDetails />}
      />
      <Route
        path="accounts/:id/manage"
        element={<AccountManage />}
      />
      <Route path="contacts" element={<ContactList />} />
      <Route path="contacts/create" element={<ContactCreate />} />
      <Route
        path="contacts/:id"
        element={<ContactDetails />}
      />
      <Route
        path="contacts/:id/manage"
        element={<ContactManage />}
      />
    </Route>

    {/* ==========================================================
        PROJECT MANAGEMENT ROUTES — replaces Project plugin
        ========================================================== */}
    <Route path="projects">
      <Route index element={<ProjectDashboard />} />
      <Route path="tasks" element={<TaskList />} />
      <Route path="tasks/create" element={<TaskCreate />} />
      <Route path="tasks/:taskId" element={<TaskDetails />} />
      <Route path="tasks/:taskId/manage" element={<TaskManage />} />
      <Route path="tasks/:taskId/edit" element={<TaskManage />} />
      <Route path="timelogs" element={<TimelogList />} />
      <Route path="timelogs/create" element={<TimelogCreate />} />
      <Route path="timesheet" element={<TimesheetView />} />
      <Route path="comments" element={<CommentListRoute />} />
      <Route path="feed" element={<FeedList />} />
      <Route
        path="reports/monthly"
        element={<MonthlyTimelogReport />}
      />
      {/* Per-project routes for E2E test navigation */}
      <Route path=":projectId" element={<TaskList />} />
      <Route path=":projectId/tasks" element={<TaskList />} />
      <Route path=":projectId/tasks/create" element={<TaskCreate />} />
      <Route path=":projectId/tasks/:taskId" element={<TaskDetails />} />
      <Route path=":projectId/tasks/:taskId/manage" element={<TaskManage />} />
      <Route path=":projectId/tasks/:taskId/edit" element={<TaskManage />} />
      <Route path=":projectId/timelogs" element={<TimelogList />} />
      <Route path=":projectId/timelogs/create" element={<TimelogCreate />} />
      <Route path=":projectId/timesheet" element={<TimesheetView />} />
      <Route path=":projectId/comments" element={<CommentListRoute />} />
    </Route>

    {/* ==========================================================
        ENTITY MANAGEMENT ROUTES
        ========================================================== */}
    <Route path="entities">
      <Route index element={<EntityList />} />
      <Route path="create" element={<EntityCreate />} />
      <Route path=":entityId" element={<EntityDetails />} />
      <Route path=":entityId/manage" element={<EntityManage />} />
      <Route path=":entityId/fields" element={<FieldList />} />
      <Route
        path=":entityId/fields/create"
        element={<FieldCreate />}
      />
      <Route
        path=":entityId/fields/:fieldId"
        element={<FieldDetails />}
      />
      <Route
        path=":entityId/fields/:fieldId/manage"
        element={<FieldManage />}
      />
      <Route path=":entityId/relations" element={<RelationList />} />
      <Route
        path=":entityId/relations/create"
        element={<RelationCreate />}
      />
      <Route
        path=":entityId/relations/:relationId"
        element={<RelationDetails />}
      />
      <Route
        path=":entityId/relations/:relationId/manage"
        element={<RelationManage />}
      />
    </Route>

    {/* ==========================================================
        INVOICING ROUTES
        ========================================================== */}
    <Route path="invoicing">
      <Route path="invoices" element={<InvoiceList />} />
      <Route path="invoices/create" element={<InvoiceCreate />} />
      <Route
        path="invoices/:invoiceId"
        element={<InvoiceDetails />}
      />
      <Route
        path="invoices/:invoiceId/manage"
        element={<InvoiceManage />}
      />
      <Route path="quotes" element={<QuoteList />} />
      <Route path="quotes/create" element={<QuoteCreate />} />
      <Route path="quotes/:quoteId" element={<QuoteDetails />} />
      <Route path="payments" element={<PaymentList />} />
      <Route path="payments/create" element={<PaymentCreate />} />
      <Route
        path="payments/:paymentId"
        element={<PaymentDetails />}
      />
    </Route>

    {/* ==========================================================
        INVENTORY ROUTES
        ========================================================== */}
    <Route path="inventory">
      <Route path="products" element={<ProductList />} />
      <Route path="products/create" element={<ProductCreate />} />
      <Route
        path="products/:productId"
        element={<ProductDetails />}
      />
      <Route
        path="products/:productId/manage"
        element={<ProductManage />}
      />
      <Route path="stock" element={<StockList />} />
      <Route path="stock/adjust" element={<StockAdjustment />} />
    </Route>

    {/* ==========================================================
        REPORTING & ANALYTICS ROUTES
        ========================================================== */}
    <Route path="reports">
      <Route index element={<DashboardList />} />
      <Route
        path="dashboards/:dashboardId"
        element={<DashboardView />}
      />
      <Route path="create" element={<ReportCreate />} />
      <Route
        path=":reportId/manage"
        element={<ReportManage />}
      />
      <Route path="analytics" element={<AnalyticsOverview />} />
    </Route>

    {/* ==========================================================
        NOTIFICATION ROUTES — replaces Mail plugin
        ========================================================== */}
    <Route path="notifications">
      <Route index element={<NotificationCenter />} />
      <Route path="emails" element={<EmailList />} />
      <Route path="emails/compose" element={<EmailCompose />} />
      <Route path="emails/:emailId" element={<EmailDetails />} />
      <Route path="smtp" element={<SmtpServiceList />} />
      <Route path="smtp/create" element={<SmtpServiceCreate />} />
      <Route
        path="smtp/:serviceId/manage"
        element={<SmtpServiceManage />}
      />
    </Route>

    {/* ==========================================================
        FILE MANAGEMENT ROUTES
        ========================================================== */}
    <Route path="files">
      <Route index element={<FileList />} />
      <Route path="upload" element={<FileUpload />} />
      <Route path=":fileId" element={<FileDetails />} />
    </Route>

    {/* ==========================================================
        WORKFLOW ROUTES
        ========================================================== */}
    <Route path="workflows">
      <Route index element={<WorkflowList />} />
      <Route path="create" element={<WorkflowCreate />} />
      <Route
        path=":workflowId"
        element={<WorkflowDetails />}
      />
      <Route
        path=":workflowId/manage"
        element={<WorkflowManage />}
      />
      <Route path="schedules" element={<ScheduleList />} />
      <Route
        path="schedules/:scheduleId/manage"
        element={<ScheduleManage />}
      />
      <Route path="executions" element={<ExecutionList />} />
      <Route
        path="executions/:executionId"
        element={<ExecutionDetails />}
      />
    </Route>

    {/* ==========================================================
        PLUGIN / EXTENSION ROUTES
        ========================================================== */}
    <Route path="plugins">
      <Route index element={<PluginList />} />
      <Route path=":pluginId" element={<PluginDetails />} />
      <Route
        path=":pluginId/manage"
        element={<PluginManage />}
      />
    </Route>

    {/* ==========================================================
        DYNAMIC ERP ROUTES
        These match the monolith's Razor Page @page directives
        exactly, translating ASP.NET route parameters to React
        Router dynamic segments.
        ========================================================== */}

    {/* Application home — replaces ApplicationHome.cshtml
        Route: /{AppName}/a/{PageName?} */}
    <Route path=":appName/a" element={<AppHome />} />
    <Route path=":appName/a/:pageName" element={<AppHome />} />

    {/* Application node — replaces ApplicationNode.cshtml
        Route: /{AppName}/{AreaName}/{NodeName}/a/{PageName?} */}
    <Route
      path=":appName/:areaName/:nodeName/a"
      element={<AppNode />}
    />
    <Route
      path=":appName/:areaName/:nodeName/a/:pageName"
      element={<AppNode />}
    />

    {/* Record list — replaces RecordList.cshtml
        Route: /{AppName}/{AreaName}/{NodeName}/l/{PageName?} */}
    <Route
      path=":appName/:areaName/:nodeName/l"
      element={<RecordList />}
    />
    <Route
      path=":appName/:areaName/:nodeName/l/:pageName"
      element={<RecordList />}
    />

    {/* Record create — replaces RecordCreate.cshtml
        Route: /{AppName}/{AreaName}/{NodeName}/c/{PageName?} */}
    <Route
      path=":appName/:areaName/:nodeName/c"
      element={<RecordCreate />}
    />
    <Route
      path=":appName/:areaName/:nodeName/c/:pageName"
      element={<RecordCreate />}
    />

    {/* Record details — replaces RecordDetails.cshtml
        Route: /{AppName}/{AreaName}/{NodeName}/r/{RecordId}/{PageName?} */}
    <Route
      path=":appName/:areaName/:nodeName/r/:recordId"
      element={<RecordDetails />}
    />
    <Route
      path=":appName/:areaName/:nodeName/r/:recordId/:pageName"
      element={<RecordDetails />}
    />

    {/* Record manage — replaces RecordManage.cshtml
        Route: /{AppName}/{AreaName}/{NodeName}/m/{RecordId}/{PageName?} */}
    <Route
      path=":appName/:areaName/:nodeName/m/:recordId"
      element={<RecordManage />}
    />
    <Route
      path=":appName/:areaName/:nodeName/m/:recordId/:pageName"
      element={<RecordManage />}
    />

    {/* ==========================================================
        RELATED RECORD ROUTES (nested under record details)
        These replace RecordRelatedRecords*.cshtml pages.
        Route pattern:
          /{App}/{Area}/{Node}/r/{RecordId}/rl/{RelationId}/…
        ========================================================== */}

    {/* Related records list — replaces RecordRelatedRecordsList.cshtml */}
    <Route
      path=":appName/:areaName/:nodeName/r/:recordId/rl/:relationId/l"
      element={<RecordRelatedRecordsList />}
    />
    <Route
      path=":appName/:areaName/:nodeName/r/:recordId/rl/:relationId/l/:pageName"
      element={<RecordRelatedRecordsList />}
    />

    {/* Related record create — replaces RecordRelatedRecordCreate.cshtml */}
    <Route
      path=":appName/:areaName/:nodeName/r/:recordId/rl/:relationId/c"
      element={<RecordRelatedRecordCreate />}
    />
    <Route
      path=":appName/:areaName/:nodeName/r/:recordId/rl/:relationId/c/:pageName"
      element={<RecordRelatedRecordCreate />}
    />

    {/* Related record details — replaces RecordRelatedRecordDetails.cshtml */}
    <Route
      path=":appName/:areaName/:nodeName/r/:recordId/rl/:relationId/r/:relatedRecordId"
      element={<RecordRelatedRecordDetails />}
    />
    <Route
      path=":appName/:areaName/:nodeName/r/:recordId/rl/:relationId/r/:relatedRecordId/:pageName"
      element={<RecordRelatedRecordDetails />}
    />

    {/* Related record manage — replaces RecordRelatedRecordManage.cshtml */}
    <Route
      path=":appName/:areaName/:nodeName/r/:recordId/rl/:relationId/m/:relatedRecordId"
      element={<RecordRelatedRecordManage />}
    />
    <Route
      path=":appName/:areaName/:nodeName/r/:recordId/rl/:relationId/m/:relatedRecordId/:pageName"
      element={<RecordRelatedRecordManage />}
    />
  </Route>
</Route>

{/* ================================================================
    STANDALONE RECORD ROUTES — E2E test entry points
    Allows direct navigation to /records/:entityName for record
    list, create, details, and manage views without requiring
    the full app/area/node URL structure.
    ================================================================ */}
    <Route path="records/:entityName" element={<RecordList />} />
    <Route path="records/:entityName/create" element={<RecordCreate />} />
    <Route path="records/:entityName/:recordId" element={<RecordDetails />} />
    <Route path="records/:entityName/:recordId/manage" element={<RecordManage />} />
    <Route path="records/:entityName/:recordId/edit" element={<RecordManage />} />

{/* ================================================================
    CATCH-ALL — redirect unknown paths to dashboard
    ================================================================ */}
<Route path="*" element={<Navigate to="/" replace />} />
    </Route>,
  ),
);
