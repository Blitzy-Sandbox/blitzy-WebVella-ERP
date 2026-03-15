/**
 * ApplicationDetails — Read-Only Application Details Page
 *
 * React page replacing `WebVella.Erp.Plugins.SDK/Pages/application/details.cshtml[.cs]`.
 * Displays all application metadata fields in read-only mode at the route
 * `/admin/applications/:appId`.
 *
 * Source mapping:
 *   - DetailsModel.PageInit() — loads App via AppService, builds RoleOptions, LocalNav
 *   - DetailsModel.OnGet()    — initialization + page render
 *   - DetailsModel.OnPost()   — delete app with ValidationException handling
 *   - details.cshtml           — wv-form (Display mode), page header, local nav tabs
 *
 * Key transformations:
 *   - `AppService.GetApplication(RecordId)`          → `useApp(appId)` TanStack Query
 *   - `SecurityManager().GetAllRoles()`              → `useRoles()` TanStack Query
 *   - `appServ.DeleteApplication(App.Id)`            → `useDeleteApp().mutate({ id })`
 *   - `AdminPageUtils.GetAppAdminSubNav(App, "details")` → TabNav sub-navigation
 *   - `wv-form mode="Display"` fields               → FieldDisplay helper components
 *   - JavaScript `confirm()` dialog                  → Modal confirmation component
 *   - `Redirect(ReturnUrl)`                          → `navigate('/admin/applications')`
 *
 * @module pages/admin/ApplicationDetails
 */

import { useState, useMemo, useCallback } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';

import { useApp, useDeleteApp } from '../../hooks/useApps';
import { useRoles } from '../../hooks/useUsers';
import type { App } from '../../types/app';
import type { ErpRole } from '../../types/user';
import Modal from '../../components/common/Modal';
import TabNav, { type TabConfig } from '../../components/common/TabNav';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * Application admin sub-navigation tab definitions.
 *
 * Replaces `AdminPageUtils.GetAppAdminSubNav(App, "details")` from the
 * monolith which generated raw HTML tab link elements for the header
 * toolbar. Each tab maps to a distinct application admin section route:
 *   - Details  → `/admin/applications/:appId`          (this page)
 *   - Manage   → `/admin/applications/:appId/manage`
 *   - Pages    → `/admin/applications/:appId/pages`
 *   - Sitemap  → `/admin/applications/:appId/sitemap`
 */
const APP_ADMIN_TABS: TabConfig[] = [
  { id: 'details', label: 'Details' },
  { id: 'manage', label: 'Manage' },
  { id: 'pages', label: 'Pages' },
  { id: 'sitemap', label: 'Sitemap' },
];

/** Number of visible tabs in the sub-navigation bar */
const VISIBLE_TABS_COUNT = APP_ADMIN_TABS.length;

// ---------------------------------------------------------------------------
// Sub-components — Display-only field renderers
// ---------------------------------------------------------------------------

/** Props for the read-only text field display */
interface FieldDisplayProps {
  /** Label text displayed above the value */
  label: string;
  /** Value text displayed below the label */
  value: string;
  /** Whether to display a required indicator after the label */
  required?: boolean;
}

/**
 * Read-only text field display.
 * Replaces `<wv-field-text>` and `<wv-field-textarea>` in Display mode
 * from the monolith's details.cshtml.
 */
function FieldDisplay({
  label,
  value,
  required = false,
}: FieldDisplayProps): React.JSX.Element {
  return (
    <div>
      <div className="text-xs font-medium text-gray-500 uppercase tracking-wide">
        {label}
        {required && (
          <span className="text-red-500 ms-0.5" aria-hidden="true">
            *
          </span>
        )}
      </div>
      <div className="mt-1 text-sm text-gray-900">
        {value || '\u2014'}
      </div>
    </div>
  );
}

/** Props for the color field display */
interface ColorFieldDisplayProps {
  /** Label text */
  label: string;
  /** CSS colour value (hex string) */
  value: string;
}

/**
 * Read-only color field display with swatch.
 * Replaces `<wv-field-color>` in Display mode.
 */
function ColorFieldDisplay({
  label,
  value,
}: ColorFieldDisplayProps): React.JSX.Element {
  return (
    <div>
      <div className="text-xs font-medium text-gray-500 uppercase tracking-wide">
        {label}
      </div>
      <div className="mt-1 flex items-center gap-2">
        <span
          className="inline-block h-5 w-5 rounded border border-gray-300 flex-shrink-0"
          style={{ backgroundColor: value || 'transparent' }}
          aria-hidden="true"
        />
        <span className="text-sm text-gray-900 font-mono">
          {value || '\u2014'}
        </span>
      </div>
    </div>
  );
}

/** Props for the icon CSS class field display */
interface IconFieldDisplayProps {
  /** Label text */
  label: string;
  /** CSS class for the icon (e.g. "fa fa-home") */
  value: string;
}

/**
 * Read-only icon field display with icon preview.
 * Replaces `<wv-field-icon-css>` in Display mode.
 */
function IconFieldDisplay({
  label,
  value,
}: IconFieldDisplayProps): React.JSX.Element {
  return (
    <div>
      <div className="text-xs font-medium text-gray-500 uppercase tracking-wide">
        {label}
      </div>
      <div className="mt-1 flex items-center gap-2 text-sm text-gray-900">
        {value ? (
          <>
            <i className={value} aria-hidden="true" />
            <span>{value}</span>
          </>
        ) : (
          <span>{'\u2014'}</span>
        )}
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Main Component
// ---------------------------------------------------------------------------

/**
 * ApplicationDetails renders a read-only view of application metadata.
 *
 * Route: `/admin/applications/:appId`
 *
 * Fetches the application by ID from route params, displays all metadata
 * fields (Name, Label, Icon CSS Class, Color, Author, Weight, Description,
 * Access) in display mode, provides sub-navigation tabs for sibling admin
 * sections, and a delete action with confirmation modal.
 *
 * Data sources:
 *   - `useApp(appId)` — app metadata (replaces AppService.GetApplication)
 *   - `useRoles()` — role list for access display (replaces SecurityManager.GetAllRoles)
 *   - `useDeleteApp()` — deletion mutation (replaces appServ.DeleteApplication)
 */
export default function ApplicationDetails(): React.JSX.Element {
  // ── URL params & navigation ───────────────────────────────────────────
  const { appId = '' } = useParams<{ appId: string }>();
  const navigate = useNavigate();

  // ── Local state ───────────────────────────────────────────────────────
  const [showDeleteModal, setShowDeleteModal] = useState(false);

  // ── Data queries ──────────────────────────────────────────────────────
  const {
    data: appData,
    isLoading: appLoading,
    isError: appError,
    error: appErrorObj,
  } = useApp(appId);

  const { data: rolesData } = useRoles();

  const deleteAppMutation = useDeleteApp();

  // ── Derived data ──────────────────────────────────────────────────────

  /** Extract the App object from the ApiResponse envelope */
  const app: App | undefined = appData?.object;

  /**
   * Extract roles array from the API response envelope.
   * `useRoles()` returns `ApiResponse<ErpRole[]>` — the typed array
   * lives inside `data.object`.
   */
  const roles: ErpRole[] = useMemo(
    () => rolesData?.object ?? [],
    [rolesData],
  );

  /**
   * Compute human-readable role labels from the app's access array.
   *
   * Mirrors `details.cshtml.cs` PageInit() where `RoleOptions` are
   * built from `SecurityManager().GetAllRoles()` and used to display
   * the Access multiselect field in read-only mode with role name labels.
   * Role IDs that cannot be resolved fall back to showing the raw ID.
   */
  const accessRoleLabels: ReadonlyArray<{ id: string; name: string }> =
    useMemo(() => {
      if (!app?.access || roles.length === 0) return [];
      return app.access.map((roleId: string) => {
        const role = roles.find((r: ErpRole) => r.id === roleId);
        return { id: roleId, name: role?.name ?? roleId };
      });
    }, [app?.access, roles]);

  // ── Callbacks ─────────────────────────────────────────────────────────

  /** Open the delete confirmation modal */
  const handleOpenDeleteModal = useCallback((): void => {
    setShowDeleteModal(true);
  }, []);

  /** Close the delete confirmation modal */
  const handleCloseDeleteModal = useCallback((): void => {
    setShowDeleteModal(false);
  }, []);

  /**
   * Confirm application deletion.
   *
   * Invokes the delete mutation and navigates to `/admin/applications`
   * on success. Mirrors `details.cshtml.cs` OnPost() which calls
   * `appServ.DeleteApplication(App.Id)`, catches ValidationException,
   * and redirects to `/sdk/objects/application/l/list`.
   */
  const handleConfirmDelete = useCallback((): void => {
    if (!app) return;

    deleteAppMutation.mutate(
      { id: app.id },
      {
        onSuccess: () => {
          setShowDeleteModal(false);
          navigate('/admin/applications');
        },
        onError: () => {
          /* Keep modal open so user sees the error rendered below body text */
        },
      },
    );
  }, [app, deleteAppMutation, navigate]);

  /**
   * Handle sub-navigation tab changes by navigating to the corresponding
   * application admin section route.
   *
   * Mirrors `AdminPageUtils.GetAppAdminSubNav(App, "details")` from
   * the monolith which generated link elements for the header toolbar.
   */
  const handleTabChange = useCallback(
    (tabId: string): void => {
      if (tabId === 'details') return; // Already on the details page
      navigate(`/admin/applications/${appId}/${tabId}`);
    },
    [appId, navigate],
  );

  // ── Loading state ─────────────────────────────────────────────────────

  if (appLoading) {
    return (
      <div
        className="flex items-center justify-center p-12"
        role="status"
        aria-label="Loading application details"
      >
        <div className="text-gray-500 text-sm">
          Loading application details{'\u2026'}
        </div>
      </div>
    );
  }

  // ── Error / not-found state ───────────────────────────────────────────

  if (appError || !app) {
    return (
      <div className="p-6">
        <div
          className="rounded-md bg-red-50 p-4 text-sm text-red-700"
          role="alert"
        >
          {appErrorObj?.message ?? 'Application not found.'}
        </div>
      </div>
    );
  }

  // ── Render ────────────────────────────────────────────────────────────

  return (
    <div className="min-h-0 flex-1">
      {/* ────────── Page Header ────────── */}
      <header className="border-b border-gray-200 bg-white px-6 pb-0 pt-5">
        {/* Breadcrumb / back link */}
        <div className="mb-3">
          <Link
            to="/admin/applications"
            className="text-xs text-gray-500 hover:text-gray-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            &larr; Applications
          </Link>
        </div>

        {/* Title row: icon + app name + subtitle + action buttons */}
        <div className="flex flex-wrap items-start justify-between gap-4 mb-4">
          {/* Left: app icon, name, subtitle */}
          <div className="flex items-center gap-3">
            {/* Application icon badge — uses inline style for dynamic color */}
            <span
              className="flex h-10 w-10 items-center justify-center rounded-lg text-lg flex-shrink-0"
              style={{
                backgroundColor: app.color || '#6b7280',
                color: '#fff',
              }}
              aria-hidden="true"
            >
              {app.iconClass ? (
                <i className={app.iconClass} />
              ) : (
                <span className="text-sm font-bold">
                  {app.label.charAt(0).toUpperCase()}
                </span>
              )}
            </span>

            <div>
              <h1 className="text-xl font-semibold text-gray-900">
                {app.label}
              </h1>
              <p className="text-sm text-gray-500">Details</p>
            </div>
          </div>

          {/* Right: action buttons */}
          <nav
            aria-label="Application actions"
            className="flex items-center gap-2"
          >
            {/* Delete action */}
            <button
              type="button"
              onClick={handleOpenDeleteModal}
              className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-red-600 shadow-sm hover:bg-red-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
            >
              <svg
                className="h-4 w-4"
                fill="none"
                viewBox="0 0 24 24"
                strokeWidth={1.5}
                stroke="currentColor"
                aria-hidden="true"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="M14.74 9l-.346 9m-4.788 0L9.26 9m9.968-3.21c.342.052.682.107 1.022.166m-1.022-.165L18.16 19.673a2.25 2.25 0 01-2.244 2.077H8.084a2.25 2.25 0 01-2.244-2.077L4.772 5.79m14.456 0a48.108 48.108 0 00-3.478-.397m-12 .562c.34-.059.68-.114 1.022-.165m0 0a48.11 48.11 0 013.478-.397m7.5 0v-.916c0-1.18-.91-2.164-2.09-2.201a51.964 51.964 0 00-3.32 0c-1.18.037-2.09 1.022-2.09 2.201v.916m7.5 0a48.667 48.667 0 00-7.5 0"
                />
              </svg>
              Delete App
            </button>

            {/* Manage link — navigates to the application edit page */}
            <Link
              to={`/admin/applications/${appId}/manage`}
              className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            >
              <svg
                className="h-4 w-4 text-orange-500"
                fill="none"
                viewBox="0 0 24 24"
                strokeWidth={1.5}
                stroke="currentColor"
                aria-hidden="true"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="M9.594 3.94c.09-.542.56-.94 1.11-.94h2.593c.55 0 1.02.398 1.11.94l.213 1.281c.063.374.313.686.645.87.074.04.147.083.22.127.324.196.72.257 1.075.124l1.217-.456a1.125 1.125 0 011.37.49l1.296 2.247a1.125 1.125 0 01-.26 1.431l-1.003.827c-.293.24-.438.613-.431.992a6.759 6.759 0 010 .255c-.007.378.138.75.43.99l1.005.828c.424.35.534.954.26 1.43l-1.298 2.247a1.125 1.125 0 01-1.369.491l-1.217-.456c-.355-.133-.75-.072-1.076.124a6.57 6.57 0 01-.22.128c-.331.183-.581.495-.644.869l-.213 1.28c-.09.543-.56.941-1.11.941h-2.594c-.55 0-1.02-.398-1.11-.94l-.213-1.281c-.062-.374-.312-.686-.644-.87a6.52 6.52 0 01-.22-.127c-.325-.196-.72-.257-1.076-.124l-1.217.456a1.125 1.125 0 01-1.369-.49l-1.297-2.247a1.125 1.125 0 01.26-1.431l1.004-.827c.292-.24.437-.613.43-.992a6.932 6.932 0 010-.255c.007-.378-.138-.75-.43-.99l-1.004-.828a1.125 1.125 0 01-.26-1.43l1.297-2.247a1.125 1.125 0 011.37-.491l1.216.456c.356.133.751.072 1.076-.124.072-.044.146-.087.22-.128.332-.183.582-.495.644-.869l.214-1.281z"
                />
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="M15 12a3 3 0 11-6 0 3 3 0 016 0z"
                />
              </svg>
              Manage
            </Link>
          </nav>
        </div>

        {/* Sub-navigation tabs */}
        <TabNav
          tabs={APP_ADMIN_TABS}
          activeTabId="details"
          onTabChange={handleTabChange}
          visibleTabs={VISIBLE_TABS_COUNT}
        />
      </header>

      {/* ────────── Application Metadata (Display Mode) ────────── */}
      <main className="px-6 py-6">
        <section
          className="rounded-lg border border-gray-200 bg-white p-6"
          aria-label="Application metadata"
        >
          <div className="grid grid-cols-12 gap-x-6 gap-y-5">
            {/* Row 1: Name | Label */}
            <div className="col-span-12 sm:col-span-6">
              <FieldDisplay label="Name" value={app.name} required />
            </div>
            <div className="col-span-12 sm:col-span-6">
              <FieldDisplay label="Label" value={app.label} required />
            </div>

            {/* Row 2: Icon CSS Class | Color */}
            <div className="col-span-12 sm:col-span-6">
              <IconFieldDisplay
                label="Icon CSS Class"
                value={app.iconClass}
              />
            </div>
            <div className="col-span-12 sm:col-span-6">
              <ColorFieldDisplay label="Color" value={app.color} />
            </div>

            {/* Row 3: Author | Weight */}
            <div className="col-span-12 sm:col-span-6">
              <FieldDisplay label="Author" value={app.author} />
            </div>
            <div className="col-span-12 sm:col-span-6">
              <FieldDisplay
                label="Weight"
                value={String(app.weight ?? 0)}
              />
            </div>

            {/* Row 4: Description (full width) */}
            <div className="col-span-12">
              <FieldDisplay label="Description" value={app.description} />
            </div>

            {/* Row 5: Access roles (multiselect display, full width) */}
            <div className="col-span-12">
              <div className="text-xs font-medium text-gray-500 uppercase tracking-wide">
                Access
              </div>
              <div className="mt-1">
                {accessRoleLabels.length > 0 ? (
                  <div className="flex flex-wrap gap-1.5">
                    {accessRoleLabels.map((roleLabel) => (
                      <span
                        key={roleLabel.id}
                        className="inline-flex items-center rounded-full bg-blue-100 px-2.5 py-0.5 text-xs font-medium text-blue-800"
                      >
                        {roleLabel.name}
                      </span>
                    ))}
                  </div>
                ) : (
                  <span className="text-sm text-gray-400">
                    {'\u2014'}
                  </span>
                )}
              </div>
            </div>
          </div>
        </section>
      </main>

      {/* ────────── Delete Confirmation Modal ────────── */}
      <Modal
        isVisible={showDeleteModal}
        title="Delete Application"
        onClose={handleCloseDeleteModal}
        footer={
          <div className="flex justify-end gap-3">
            <button
              type="button"
              onClick={handleCloseDeleteModal}
              className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={handleConfirmDelete}
              disabled={deleteAppMutation.isPending}
              className="rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:opacity-50 disabled:cursor-not-allowed"
            >
              {deleteAppMutation.isPending
                ? 'Deleting\u2026'
                : 'Delete'}
            </button>
          </div>
        }
      >
        <p className="text-sm text-gray-600">
          Are you sure you want to delete this app? This action is permanent
          and will remove all associated pages and sitemap entries.
        </p>
        {deleteAppMutation.isError && (
          <div
            className="mt-3 rounded-md bg-red-50 p-3 text-sm text-red-700"
            role="alert"
          >
            {deleteAppMutation.error?.message ??
              'Failed to delete application.'}
          </div>
        )}
      </Modal>
    </div>
  );
}
