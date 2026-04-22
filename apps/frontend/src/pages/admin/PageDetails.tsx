/**
 * PageDetails — Admin Page Definition Details (Read-Only)
 *
 * Route: /admin/pages/:pageId
 *
 * Replaces WebVella.Erp.Plugins.SDK/Pages/page/details.cshtml[.cs]
 *
 * Source mapping:
 *   DetailsModel.PageInit()        → usePage(id) query + derived computations
 *   DetailsModel.OnGet()           → component mount + data fetching
 *   DetailsModel.OnPost(op=delete) → useDeletePage() mutation + Modal confirmation
 *   DetailsModel.OnPost(op=clone)  → useClonePage() mutation + Modal confirmation
 *   AdminPageUtils.GetPageAdminSubNav() → TabNav sub-navigation
 *   PageUtils.CalculatePageUrl()   → useMemo computed page public URL
 *   SecurityManager.GetAllRoles()  → useRoles() for access display
 *
 * @module pages/admin/PageDetails
 */

import { useState, useCallback, useMemo } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';

import { usePage, useDeletePage, useClonePage } from '../../hooks/usePages';
import { useRoles } from '../../hooks/useUsers';
import Modal, { ModalSize } from '../../components/common/Modal';
import TabNav from '../../components/common/TabNav';
import type { TabConfig } from '../../components/common/TabNav';
import Button, { ButtonColor } from '../../components/common/Button';
import type { ErpPage } from '../../types/page';
import { PageType } from '../../types/page';
import type { ErpRole } from '../../types/user';

/* ------------------------------------------------------------------ */
/*  Helpers                                                            */
/* ------------------------------------------------------------------ */

/**
 * Extended page data that may include access restrictions from the API.
 * The access field represents role IDs inherited from the parent
 * application (mirrors monolith's app.Access property).
 */
interface PageWithAccess extends ErpPage {
  access?: string[];
  description?: string;
}

/** Human-readable labels keyed by PageType enum values. */
const PAGE_TYPE_LABELS: Record<PageType, string> = {
  [PageType.Home]: 'Home',
  [PageType.Site]: 'Site',
  [PageType.Application]: 'Application',
  [PageType.RecordList]: 'Record List',
  [PageType.RecordCreate]: 'Record Create',
  [PageType.RecordDetails]: 'Record Details',
  [PageType.RecordManage]: 'Record Manage',
};

/**
 * Computes the public URL for a page based on its type and hierarchy.
 *
 * Replaces PageUtils.CalculatePageUrl(Guid pageId) from the monolith
 * which resolved the complete URL path by looking up the app / area /
 * node hierarchy. In the React SPA the URL pattern is:
 *   Home        → /
 *   Site        → /s/{pageName}
 *   Application → /app/{appId}/area/{areaId}/node/{nodeId}/{pageName}
 *   Record*     → same as Application
 */
function calculatePageUrl(page: ErpPage): string {
  switch (page.type) {
    case PageType.Home:
      return '/';

    case PageType.Site:
      return `/s/${encodeURIComponent(page.name)}`;

    case PageType.Application:
    case PageType.RecordList:
    case PageType.RecordCreate:
    case PageType.RecordDetails:
    case PageType.RecordManage: {
      if (!page.appId) return '';
      const parts: string[] = ['/app', page.appId];
      if (page.areaId) {
        parts.push('area', page.areaId);
      }
      if (page.nodeId) {
        parts.push('node', page.nodeId);
      }
      parts.push(encodeURIComponent(page.name));
      return parts.join('/');
    }

    default:
      return '';
  }
}

/** Returns a human-readable label for a PageType value. */
function getPageTypeLabel(type: PageType): string {
  return PAGE_TYPE_LABELS[type] ?? 'Unknown';
}

/* ------------------------------------------------------------------ */
/*  Sub-component — read-only field display                            */
/* ------------------------------------------------------------------ */

interface FieldDisplayProps {
  /** Label shown above the value. */
  label: string;
  /** Raw string value to display when no children are provided. */
  value: string;
  /** Whether the field is required (shows red asterisk). */
  required?: boolean;
  /** Optional custom renderer — takes priority over `value`. */
  children?: React.ReactNode;
}

/**
 * Lightweight read-only field display used for all metadata rows.
 * Renders label + value (or children) with consistent Tailwind styling.
 */
function FieldDisplay({
  label,
  value,
  required = false,
  children,
}: FieldDisplayProps): React.ReactElement {
  return (
    <div>
      <span className="block text-sm font-medium text-gray-700">
        {label}
        {required && (
          <span className="ml-1 text-red-500" aria-label="required">
            *
          </span>
        )}
      </span>

      {children ? (
        <div className="mt-1 text-sm text-gray-900">{children}</div>
      ) : (
        <p className="mt-1 text-sm text-gray-900">
          {value || (
            <span className="italic text-gray-400" aria-label="empty">
              —
            </span>
          )}
        </p>
      )}
    </div>
  );
}

/* ------------------------------------------------------------------ */
/*  Main component                                                     */
/* ------------------------------------------------------------------ */

/**
 * PageDetails — read-only page definition view with admin actions.
 *
 * Displays all page metadata fields in read-only mode with action
 * buttons for Visit Page, Manage Details, Clone, and Delete operations.
 * Sub-navigation tabs link to related admin views (Custom Body,
 * Generated Body, Model).
 */
export default function PageDetails(): React.ReactElement {
  /* ── Route params & navigation ─────────────────────────────── */
  const { pageId } = useParams<{ pageId: string }>();
  const navigate = useNavigate();

  /* ── Data fetching ─────────────────────────────────────────── */
  const {
    data: pageResponse,
    isLoading: isPageLoading,
    isError: isPageError,
  } = usePage(pageId);
  const { data: rolesResponse } = useRoles();

  /* ── Mutations ─────────────────────────────────────────────── */
  const deleteMutation = useDeletePage();
  const cloneMutation = useClonePage();

  /* ── Modal visibility state ────────────────────────────────── */
  const [showDeleteModal, setShowDeleteModal] = useState(false);
  const [showCloneModal, setShowCloneModal] = useState(false);

  /* ── Derived data ──────────────────────────────────────────── */
  const page = pageResponse?.object as PageWithAccess | undefined;
  const roles: ErpRole[] = rolesResponse?.object ?? [];

  /* ── Computed: page public URL ─────────────────────────────── */
  const pagePublicUrl = useMemo<string>(() => {
    if (!page) return '';
    return calculatePageUrl(page);
  }, [page]);

  /* ── Computed: access restrictions display ──────────────────── */
  const accessDisplay = useMemo<string>(() => {
    const accessIds = page?.access;
    if (!accessIds || accessIds.length === 0) return '';
    return accessIds
      .map((roleId: string) => {
        const match = roles.find((r: ErpRole) => r.id === roleId);
        return match ? match.name : roleId;
      })
      .join(', ');
  }, [page, roles]);

  /* ── Computed: sub-nav tabs ────────────────────────────────── */
  const subNavTabs = useMemo<TabConfig[]>(
    () => [
      { id: 'details', label: 'Details' },
      { id: 'custom-body', label: 'Custom Body' },
      { id: 'generated-body', label: 'Generated Body' },
      { id: 'model', label: 'Model' },
    ],
    [],
  );

  /* ── Computed: icon class ──────────────────────────────────── */
  const pageIconClass = useMemo<string>(
    () => page?.iconClass || 'fa fa-file',
    [page],
  );

  /* ── Event handlers ────────────────────────────────────────── */
  const handleDelete = useCallback(() => {
    setShowDeleteModal(true);
  }, []);

  const handleClone = useCallback(() => {
    setShowCloneModal(true);
  }, []);

  const handleConfirmDelete = useCallback(async () => {
    if (!pageId) return;
    try {
      await deleteMutation.mutateAsync(pageId);
      setShowDeleteModal(false);
      navigate('/admin/pages');
    } catch {
      /* Error state is surfaced by the mutation hook */
    }
  }, [pageId, deleteMutation, navigate]);

  const handleConfirmClone = useCallback(async () => {
    if (!pageId) return;
    try {
      const result = await cloneMutation.mutateAsync({ id: pageId });
      setShowCloneModal(false);
      const clonedPage = result?.object;
      if (clonedPage?.id) {
        navigate(`/admin/pages/${clonedPage.id}`);
      } else {
        navigate('/admin/pages');
      }
    } catch {
      /* Error state is surfaced by the mutation hook */
    }
  }, [pageId, cloneMutation, navigate]);

  const handleManageDetails = useCallback(() => {
    if (!pageId) return;
    navigate(`/admin/pages/${pageId}/manage`);
  }, [pageId, navigate]);

  const handleTabChange = useCallback(
    (tabId: string) => {
      if (!pageId) return;
      const routes: Record<string, string> = {
        details: `/admin/pages/${pageId}`,
        'custom-body': `/admin/pages/${pageId}/custom-body`,
        'generated-body': `/admin/pages/${pageId}/generated-body`,
        model: `/admin/pages/${pageId}/model`,
      };
      const route = routes[tabId];
      if (route) navigate(route);
    },
    [pageId, navigate],
  );

  /* ── Loading state ─────────────────────────────────────────── */
  if (isPageLoading) {
    return (
      <div
        className="flex min-h-[12.5rem] items-center justify-center"
        role="status"
        aria-label="Loading page details"
      >
        <span className="text-sm text-gray-500">Loading page details…</span>
      </div>
    );
  }

  /* ── Error / not-found state ───────────────────────────────── */
  if (isPageError || !page) {
    return (
      <div className="flex min-h-[12.5rem] flex-col items-center justify-center gap-4">
        <span className="text-sm text-gray-500">
          The requested page could not be found.
        </span>
        <Link
          to="/admin/pages"
          className="text-sm text-blue-600 underline hover:text-blue-800"
        >
          Back to Pages
        </Link>
      </div>
    );
  }

  /* ── Render ────────────────────────────────────────────────── */
  return (
    <div className="flex flex-col">
      {/* ── Page Header ──────────────────────────────────────── */}
      <header
        className="border-b-4 bg-white px-6 py-4"
        style={{ borderBottomColor: '#dc3545' }}
      >
        <div className="flex flex-wrap items-center justify-between gap-4">
          {/* Left: back link + icon + title */}
          <div className="flex items-center gap-3">
            <Link
              to="/admin/pages"
              className="text-gray-400 transition-colors duration-150 hover:text-gray-600"
              aria-label="Back to pages list"
            >
              <i className="fa fa-arrow-left" aria-hidden="true" />
            </Link>

            <span className="text-lg" style={{ color: '#dc3545' }}>
              <i className={pageIconClass} aria-hidden="true" />
            </span>

            <div className="flex flex-col">
              <span className="text-xs font-medium uppercase tracking-wide text-gray-500">
                Pages
              </span>
              <h1 className="text-lg font-semibold leading-tight text-gray-900">
                {page.label}
                <span className="ml-2 text-sm font-normal text-gray-500">
                  Details
                </span>
              </h1>
            </div>
          </div>

          {/* Right: action buttons */}
          <div className="flex flex-wrap items-center gap-2">
            <Button
              color={ButtonColor.Danger}
              onClick={handleDelete}
              iconClass="fa fa-trash-alt"
              isDisabled={deleteMutation.isPending}
            >
              Delete Page
            </Button>

            {pagePublicUrl && (
              <Button
                color={ButtonColor.Light}
                href={pagePublicUrl}
                newTab
                iconClass="fas fa-external-link-alt"
              >
                Visit Page
              </Button>
            )}

            <Button
              color={ButtonColor.Primary}
              onClick={handleClone}
              iconClass="fa fa-copy"
              isDisabled={cloneMutation.isPending}
            >
              Clone Page
            </Button>

            <Button
              color={ButtonColor.Primary}
              onClick={handleManageDetails}
              iconClass="fa fa-cog"
            >
              Manage Details
            </Button>
          </div>
        </div>

        {/* Sub-nav toolbar */}
        <nav className="mt-4" aria-label="Page admin navigation">
          <TabNav
            tabs={subNavTabs}
            activeTabId="details"
            onTabChange={handleTabChange}
          />
        </nav>
      </header>

      {/* ── General Section ──────────────────────────────────── */}
      <section className="mx-6 mt-6" aria-labelledby="section-general">
        <div className="overflow-hidden rounded-lg border border-gray-200 bg-white">
          <div className="border-b border-gray-200 px-4 py-3">
            <h2
              id="section-general"
              className="text-base font-semibold text-gray-900"
            >
              General
            </h2>
          </div>

          <div className="space-y-4 p-4">
            {/* Row 1: Name | Label */}
            <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
              <FieldDisplay label="Name" value={page.name} required />
              <FieldDisplay label="Label" value={page.label} required />
            </div>

            {/* Row 2: Icon CSS Class | Weight */}
            <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
              <FieldDisplay label="Icon CSS Class" value={page.iconClass ?? ''}>
                {page.iconClass ? (
                  <span className="inline-flex items-center gap-2">
                    <i className={page.iconClass} aria-hidden="true" />
                    <span>{page.iconClass}</span>
                  </span>
                ) : (
                  <span className="italic text-gray-400">—</span>
                )}
              </FieldDisplay>
              <FieldDisplay
                label="Weight"
                value={page.weight != null ? String(page.weight) : ''}
              />
            </div>

            {/* Row 3: Layout | Access */}
            <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
              <FieldDisplay label="Layout" value={page.layout ?? ''} />

              <div>
                <span className="block text-sm font-medium text-gray-700">
                  Access
                  <span className="ml-1 text-xs font-normal text-gray-400">
                    (inherited from application)
                  </span>
                </span>
                {accessDisplay ? (
                  <p className="mt-1 text-sm text-gray-900">{accessDisplay}</p>
                ) : (
                  <p className="mt-1 text-sm italic text-gray-400">
                    no access restrictions
                  </p>
                )}
              </div>
            </div>

            {/* Row 4: Description (full width) */}
            <FieldDisplay
              label="Description"
              value={(page as PageWithAccess).description ?? ''}
            />
          </div>
        </div>
      </section>

      {/* ── Sitemap Section ──────────────────────────────────── */}
      <section
        className="mx-6 mt-4 mb-6"
        aria-labelledby="section-sitemap"
      >
        <div className="overflow-hidden rounded-lg border border-gray-200 bg-white">
          <div className="border-b border-gray-200 px-4 py-3">
            <h2
              id="section-sitemap"
              className="text-base font-semibold text-gray-900"
            >
              Sitemap
            </h2>
          </div>

          <div className="space-y-4 p-4">
            {/* Row 1: Page Type | Application ID */}
            <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
              <FieldDisplay
                label="Page Type"
                value={getPageTypeLabel(page.type)}
              />
              <FieldDisplay
                label="Application ID"
                value={page.appId ?? 'None'}
              />
            </div>

            {/* Row 2: Area ID | Node ID */}
            <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
              <FieldDisplay label="Area ID" value={page.areaId ?? 'None'} />
              <FieldDisplay label="Node ID" value={page.nodeId ?? 'None'} />
            </div>

            {/* Row 3: Entity ID | System */}
            <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
              <FieldDisplay
                label="Entity ID"
                value={page.entityId ?? 'None'}
              />
              <FieldDisplay
                label="System"
                value={page.system ? 'Yes' : 'No'}
              />
            </div>

            {/* Row 4: Razor Body */}
            <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
              <FieldDisplay
                label="Is Razor Body"
                value={page.isRazorBody ? 'Yes' : 'No'}
              />
            </div>
          </div>
        </div>
      </section>

      {/* ── Delete Confirmation Modal ────────────────────────── */}
      <Modal
        isVisible={showDeleteModal}
        title="Delete Page"
        size={ModalSize.Small}
        onClose={() => setShowDeleteModal(false)}
        footer={
          <div className="flex justify-end gap-2">
            <Button
              color={ButtonColor.Light}
              onClick={() => setShowDeleteModal(false)}
            >
              Cancel
            </Button>
            <Button
              color={ButtonColor.Danger}
              onClick={handleConfirmDelete}
              isDisabled={deleteMutation.isPending}
            >
              {deleteMutation.isPending ? 'Deleting…' : 'Delete'}
            </Button>
          </div>
        }
      >
        <p className="text-sm text-gray-600">
          Are you sure you want to delete this page? This action cannot be
          undone.
        </p>
        {deleteMutation.isError && (
          <p className="mt-2 text-sm text-red-600" role="alert">
            Failed to delete the page. Please try again.
          </p>
        )}
      </Modal>

      {/* ── Clone Confirmation Modal ─────────────────────────── */}
      <Modal
        isVisible={showCloneModal}
        title="Clone Page"
        size={ModalSize.Small}
        onClose={() => setShowCloneModal(false)}
        footer={
          <div className="flex justify-end gap-2">
            <Button
              color={ButtonColor.Light}
              onClick={() => setShowCloneModal(false)}
            >
              Cancel
            </Button>
            <Button
              color={ButtonColor.Primary}
              onClick={handleConfirmClone}
              isDisabled={cloneMutation.isPending}
            >
              {cloneMutation.isPending ? 'Cloning…' : 'Clone'}
            </Button>
          </div>
        }
      >
        <p className="text-sm text-gray-600">
          Are you sure you want to clone this page? A copy will be created with
          a unique name.
        </p>
        {cloneMutation.isError && (
          <p className="mt-2 text-sm text-red-600" role="alert">
            Failed to clone the page. Please try again.
          </p>
        )}
      </Modal>
    </div>
  );
}
