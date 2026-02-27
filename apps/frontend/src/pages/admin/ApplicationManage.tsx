/**
 * ApplicationManage — Application Edit Page
 *
 * React page replacing `WebVella.Erp.Plugins.SDK/Pages/application/manage.cshtml[.cs]`.
 * Provides an edit form for an existing application at route
 * `/admin/applications/:appId/manage`.
 *
 * Source mapping:
 * - `AppService.GetApplication(RecordId)` → `useApp(appId)` (TanStack Query)
 * - `AppService.UpdateApplication(id, ...)` → `useUpdateApp().mutateAsync` (TanStack Mutation)
 * - `SecurityManager().GetAllRoles()` → `useRoles()` (TanStack Query)
 * - `AdminPageUtils.GetAppAdminSubNav(App, "manage")` → TabNav sub-navigation
 * - `ValidationException` → FormValidation with mapped ApiError details
 *
 * Fields (mirroring manage.cshtml wv-form):
 * - Name (text, required)
 * - Label (text, required)
 * - IconClass (text)
 * - Color (color picker)
 * - Author (text)
 * - Weight (number, integer)
 * - Description (textarea)
 * - Access (multiselect with role options)
 */

import { useState, useEffect, useCallback } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';

import { useApp, useUpdateApp } from '../../hooks/useApps';
import { useRoles } from '../../hooks/useUsers';

import type { App } from '../../types/app';
import type { ErpRole } from '../../types/user';

import DynamicForm from '../../components/forms/DynamicForm';
import type { FormValidation, ValidationError } from '../../components/forms/DynamicForm';
import TabNav from '../../components/common/TabNav';
import type { TabConfig } from '../../components/common/TabNav';

// ---------------------------------------------------------------------------
// Internal types
// ---------------------------------------------------------------------------

/** Shape of the form field state managed by useState. */
interface ApplicationFormState {
  name: string;
  label: string;
  description: string;
  iconClass: string;
  author: string;
  color: string;
  weight: number;
  access: string[];
}

/**
 * Matches the ApiError interface from `api/client.ts`.
 * Used to safely cast the caught error from mutation rejection.
 */
interface ApiErrorLike {
  message?: string;
  errors?: ReadonlyArray<{ key: string; value?: string; message: string }>;
  status?: number;
}

/** Default form field values for a fresh/loading state. */
const DEFAULT_FORM_STATE: ApplicationFormState = {
  name: '',
  label: '',
  description: '',
  iconClass: '',
  author: '',
  color: '#ffffff',
  weight: 10,
  access: [],
};

// ---------------------------------------------------------------------------
// Shared Tailwind class-name constants to reduce duplication
// ---------------------------------------------------------------------------

/** Base input styling used across text, number, and textarea fields. */
const INPUT_CLASS =
  'block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm ' +
  'text-gray-900 shadow-sm placeholder:text-gray-400 ' +
  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500 ' +
  'focus-visible:border-indigo-500 ' +
  'dark:border-gray-600 dark:bg-gray-800 dark:text-gray-100 ' +
  'dark:placeholder:text-gray-500 dark:focus-visible:ring-indigo-400';

/** Label styling shared across every form field. */
const LABEL_CLASS =
  'block text-sm font-medium text-gray-700 dark:text-gray-300';

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * ApplicationManage renders the edit form for an existing application.
 *
 * Route: `/admin/applications/:appId/manage`
 *
 * Replaces the monolith's `ManageModel` page-model with:
 * - `useApp(appId)` for loading current data (GET)
 * - `useUpdateApp()` for persisting edits (PUT)
 * - `useRoles()` for the Access multiselect role options
 * - `TabNav` for the app admin sub-navigation toolbar
 * - `DynamicForm` for validation display and form structure
 */
function ApplicationManage() {
  // ---------------------------------------------------------------------------
  // Route params & navigation
  // ---------------------------------------------------------------------------

  const { appId } = useParams<{ appId: string }>();
  const navigate = useNavigate();

  // ---------------------------------------------------------------------------
  // Data-fetching hooks
  // ---------------------------------------------------------------------------

  const {
    data: appData,
    isLoading: isAppLoading,
    isError: isAppError,
    error: appError,
  } = useApp(appId ?? '');

  const { data: rolesData, isLoading: isRolesLoading } = useRoles();

  const {
    mutateAsync: updateApp,
    isPending,
    isError: isUpdateError,
    error: updateError,
    isSuccess,
  } = useUpdateApp();

  // ---------------------------------------------------------------------------
  // Form state
  // ---------------------------------------------------------------------------

  const [formState, setFormState] = useState<ApplicationFormState>(DEFAULT_FORM_STATE);
  const [validation, setValidation] = useState<FormValidation>({ errors: [] });

  // ---------------------------------------------------------------------------
  // Pre-populate form when app data loads (replaces InitPage field binding)
  // ---------------------------------------------------------------------------

  useEffect(() => {
    if (appData?.object) {
      const app: App = appData.object;
      setFormState({
        name: app.name ?? '',
        label: app.label ?? '',
        description: app.description ?? '',
        iconClass: app.iconClass ?? '',
        author: app.author ?? '',
        color: app.color || '#ffffff',
        weight: app.weight ?? 10,
        access: Array.isArray(app.access) ? [...app.access] : [],
      });
    }
  }, [appData]);

  // ---------------------------------------------------------------------------
  // Navigate on successful update
  // ---------------------------------------------------------------------------

  useEffect(() => {
    if (isSuccess && appId) {
      navigate(`/admin/applications/${appId}`);
    }
  }, [isSuccess, appId, navigate]);

  // ---------------------------------------------------------------------------
  // Sync mutation-level errors to the validation banner
  // (ensures `isUpdateError` and `updateError` are consumed per schema)
  // ---------------------------------------------------------------------------

  useEffect(() => {
    if (isUpdateError && updateError) {
      const apiErr = updateError as unknown as ApiErrorLike;
      const mappedErrors: ValidationError[] = (apiErr.errors ?? []).map(
        (item) => ({
          propertyName: item.key ?? '',
          message: item.message ?? 'Validation error',
        }),
      );
      setValidation({
        message:
          apiErr.message ?? 'An error occurred while saving the application.',
        errors: mappedErrors,
      });
    }
  }, [isUpdateError, updateError]);

  // ---------------------------------------------------------------------------
  // Generic field change handler
  // ---------------------------------------------------------------------------

  const handleFieldChange = useCallback(
    (field: keyof ApplicationFormState) =>
      (
        e: React.ChangeEvent<
          HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement
        >,
      ) => {
        const value =
          field === 'weight'
            ? parseInt(e.target.value, 10) || 0
            : e.target.value;
        setFormState((prev) => ({ ...prev, [field]: value }));
      },
    [],
  );

  // ---------------------------------------------------------------------------
  // Access multiselect change handler
  // ---------------------------------------------------------------------------

  const handleAccessChange = useCallback(
    (e: React.ChangeEvent<HTMLSelectElement>) => {
      const selectedOptions = Array.from(
        e.target.selectedOptions,
        (opt) => opt.value,
      );
      setFormState((prev) => ({ ...prev, access: selectedOptions }));
    },
    [],
  );

  // ---------------------------------------------------------------------------
  // Form submission (replaces ManageModel.OnPost)
  // ---------------------------------------------------------------------------

  const handleSubmit = useCallback(
    async (e: React.FormEvent) => {
      e.preventDefault();
      setValidation({ errors: [] });

      if (!appId) {
        return;
      }

      // Client-side required field validation
      const errors: ValidationError[] = [];

      if (!formState.name.trim()) {
        errors.push({
          propertyName: 'name',
          message: 'Name is required.',
        });
      }

      if (!formState.label.trim()) {
        errors.push({
          propertyName: 'label',
          message: 'Label is required.',
        });
      }

      if (errors.length > 0) {
        setValidation({
          message: 'Please fix the following errors:',
          errors,
        });
        return;
      }

      try {
        await updateApp({
          id: appId,
          app: {
            id: appId,
            name: formState.name.trim(),
            label: formState.label.trim(),
            description: formState.description.trim(),
            iconClass: formState.iconClass.trim(),
            author: formState.author.trim(),
            color: formState.color.trim(),
            weight: formState.weight,
            access: formState.access,
          },
        });
        // On success the isSuccess effect above navigates to the detail page.
      } catch (err: unknown) {
        // Map ApiError from the rejected mutation to FormValidation shape.
        // The API client response interceptor rejects with an ApiError object
        // whose `errors` array contains ApiErrorItem { key, value, message }.
        const apiErr = err as ApiErrorLike;
        const mappedErrors: ValidationError[] = (apiErr.errors ?? []).map(
          (item) => ({
            propertyName: item.key ?? '',
            message: item.message ?? 'Validation error',
          }),
        );
        setValidation({
          message:
            apiErr.message ??
            'An error occurred while saving the application.',
          errors: mappedErrors,
        });
      }
    },
    [appId, formState, updateApp],
  );

  // ---------------------------------------------------------------------------
  // Sub-navigation tab change handler
  // ---------------------------------------------------------------------------

  const handleTabChange = useCallback(
    (tabId: string) => {
      if (!appId) {
        return;
      }
      switch (tabId) {
        case 'details':
          navigate(`/admin/applications/${appId}`);
          break;
        case 'pages':
          navigate(`/admin/applications/${appId}/pages`);
          break;
        case 'sitemap':
          navigate(`/admin/applications/${appId}/sitemap`);
          break;
        default:
          // 'manage' — current page, no navigation needed
          break;
      }
    },
    [appId, navigate],
  );

  // ---------------------------------------------------------------------------
  // Build sub-nav tabs (replaces AdminPageUtils.GetAppAdminSubNav)
  // ---------------------------------------------------------------------------

  const tabs: TabConfig[] = appId
    ? [
        {
          id: 'details',
          label: 'Details',
          content: (
            <Link to={`/admin/applications/${appId}`}>Details</Link>
          ),
        },
        {
          id: 'manage',
          label: 'Manage',
          content: (
            <Link to={`/admin/applications/${appId}/manage`}>Manage</Link>
          ),
        },
        {
          id: 'pages',
          label: 'Pages',
          content: (
            <Link to={`/admin/applications/${appId}/pages`}>Pages</Link>
          ),
        },
        {
          id: 'sitemap',
          label: 'Sitemap',
          content: (
            <Link to={`/admin/applications/${appId}/sitemap`}>Sitemap</Link>
          ),
        },
      ]
    : [];

  // ---------------------------------------------------------------------------
  // Derived data
  // ---------------------------------------------------------------------------

  /** All available roles for the Access multiselect. */
  const roles: ErpRole[] = rolesData?.object
    ? Array.isArray(rolesData.object)
      ? rolesData.object
      : []
    : [];

  // ---------------------------------------------------------------------------
  // Loading state
  // ---------------------------------------------------------------------------

  if (isAppLoading || isRolesLoading) {
    return (
      <div
        className="flex items-center justify-center p-8"
        role="status"
        aria-live="polite"
      >
        <svg
          className="mr-3 h-5 w-5 animate-spin text-indigo-500"
          xmlns="http://www.w3.org/2000/svg"
          fill="none"
          viewBox="0 0 24 24"
          aria-hidden="true"
        >
          <circle
            className="opacity-25"
            cx="12"
            cy="12"
            r="10"
            stroke="currentColor"
            strokeWidth="4"
          />
          <path
            className="opacity-75"
            fill="currentColor"
            d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
          />
        </svg>
        <span className="text-sm text-gray-500 dark:text-gray-400">
          Loading application…
        </span>
      </div>
    );
  }

  // ---------------------------------------------------------------------------
  // Error state (app fetch failure)
  // ---------------------------------------------------------------------------

  if (isAppError) {
    const errorMessage =
      (appError as unknown as ApiErrorLike)?.message ?? 'Unknown error';
    return (
      <div
        className="rounded-md bg-red-50 p-4 dark:bg-red-900/20"
        role="alert"
      >
        <div className="flex">
          <svg
            className="h-5 w-5 flex-shrink-0 text-red-400"
            xmlns="http://www.w3.org/2000/svg"
            viewBox="0 0 20 20"
            fill="currentColor"
            aria-hidden="true"
          >
            <path
              fillRule="evenodd"
              d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z"
              clipRule="evenodd"
            />
          </svg>
          <div className="ml-3">
            <h3 className="text-sm font-medium text-red-800 dark:text-red-300">
              Failed to load application
            </h3>
            <p className="mt-1 text-sm text-red-700 dark:text-red-400">
              {errorMessage}
            </p>
            <Link
              to="/admin/applications"
              className="mt-2 inline-block text-sm font-medium text-red-600 underline hover:text-red-500 dark:text-red-400"
            >
              Back to Applications
            </Link>
          </div>
        </div>
      </div>
    );
  }

  // ---------------------------------------------------------------------------
  // Not-found state
  // ---------------------------------------------------------------------------

  const app: App | undefined = appData?.object;

  if (!app) {
    return (
      <div
        className="rounded-md bg-yellow-50 p-4 dark:bg-yellow-900/20"
        role="alert"
      >
        <div className="flex">
          <svg
            className="h-5 w-5 flex-shrink-0 text-yellow-400"
            xmlns="http://www.w3.org/2000/svg"
            viewBox="0 0 20 20"
            fill="currentColor"
            aria-hidden="true"
          >
            <path
              fillRule="evenodd"
              d="M8.485 2.495c.673-1.167 2.357-1.167 3.03 0l6.28 10.875c.673 1.167-.168 2.625-1.516 2.625H3.72c-1.347 0-2.189-1.458-1.515-2.625L8.485 2.495zM10 6a.75.75 0 01.75.75v3.5a.75.75 0 01-1.5 0v-3.5A.75.75 0 0110 6zm0 9a1 1 0 100-2 1 1 0 000 2z"
              clipRule="evenodd"
            />
          </svg>
          <div className="ml-3">
            <h3 className="text-sm font-medium text-yellow-800 dark:text-yellow-300">
              Application not found
            </h3>
            <p className="mt-1 text-sm text-yellow-700 dark:text-yellow-400">
              The requested application could not be found.
            </p>
            <Link
              to="/admin/applications"
              className="mt-2 inline-block text-sm font-medium text-yellow-600 underline hover:text-yellow-500 dark:text-yellow-400"
            >
              Back to Applications
            </Link>
          </div>
        </div>
      </div>
    );
  }

  // ---------------------------------------------------------------------------
  // Main render — Edit Form
  // ---------------------------------------------------------------------------

  return (
    <section className="mx-auto max-w-4xl px-4 py-6 sm:px-6 lg:px-8">
      {/* Page header */}
      <header className="mb-6">
        <nav aria-label="Breadcrumb" className="mb-2">
          <ol className="flex items-center gap-1.5 text-sm text-gray-500 dark:text-gray-400">
            <li>
              <Link
                to="/admin/applications"
                className="hover:text-gray-700 dark:hover:text-gray-200"
              >
                Applications
              </Link>
            </li>
            <li aria-hidden="true">/</li>
            <li>
              <Link
                to={`/admin/applications/${appId}`}
                className="hover:text-gray-700 dark:hover:text-gray-200"
              >
                {app.label || app.name}
              </Link>
            </li>
            <li aria-hidden="true">/</li>
            <li
              className="font-medium text-gray-900 dark:text-white"
              aria-current="page"
            >
              Manage
            </li>
          </ol>
        </nav>

        <h1 className="text-2xl font-semibold text-gray-900 dark:text-white">
          Manage Application
        </h1>
        <p className="mt-1 text-sm text-gray-500 dark:text-gray-400">
          Edit the properties of the <strong>{app.label}</strong> application.
        </p>
      </header>

      {/* Sub-navigation tabs (replaces AdminPageUtils.GetAppAdminSubNav) */}
      <div className="mb-6">
        <TabNav
          tabs={tabs}
          activeTabId="manage"
          onTabChange={handleTabChange}
        />
      </div>

      {/* Application edit form */}
      <DynamicForm
        name="ApplicationManageForm"
        validation={validation}
        onSubmit={handleSubmit}
      >
        <div className="space-y-6">
          {/* ---- Name field (required) ---- */}
          <div>
            <label htmlFor="app-name" className={LABEL_CLASS}>
              Name <span className="text-red-500" aria-hidden="true">*</span>
            </label>
            <input
              id="app-name"
              name="name"
              type="text"
              required
              autoComplete="off"
              value={formState.name}
              onChange={handleFieldChange('name')}
              className={INPUT_CLASS}
              aria-required="true"
              aria-describedby="app-name-help"
            />
            <p
              id="app-name-help"
              className="mt-1 text-xs text-gray-500 dark:text-gray-400"
            >
              Unique machine name for the application (no spaces).
            </p>
          </div>

          {/* ---- Label field (required) ---- */}
          <div>
            <label htmlFor="app-label" className={LABEL_CLASS}>
              Label <span className="text-red-500" aria-hidden="true">*</span>
            </label>
            <input
              id="app-label"
              name="label"
              type="text"
              required
              autoComplete="off"
              value={formState.label}
              onChange={handleFieldChange('label')}
              className={INPUT_CLASS}
              aria-required="true"
              aria-describedby="app-label-help"
            />
            <p
              id="app-label-help"
              className="mt-1 text-xs text-gray-500 dark:text-gray-400"
            >
              Human-readable display name for the application.
            </p>
          </div>

          {/* ---- IconClass field ---- */}
          <div>
            <label htmlFor="app-icon-class" className={LABEL_CLASS}>
              Icon Class
            </label>
            <input
              id="app-icon-class"
              name="iconClass"
              type="text"
              autoComplete="off"
              value={formState.iconClass}
              onChange={handleFieldChange('iconClass')}
              className={INPUT_CLASS}
              aria-describedby="app-icon-help"
            />
            <p
              id="app-icon-help"
              className="mt-1 text-xs text-gray-500 dark:text-gray-400"
            >
              CSS class name for the application icon (e.g.
              &quot;fa fa-cogs&quot;).
            </p>
          </div>

          {/* ---- Color field ---- */}
          <div>
            <label htmlFor="app-color" className={LABEL_CLASS}>
              Color
            </label>
            <div className="flex items-center gap-3">
              <input
                id="app-color"
                name="color"
                type="color"
                value={formState.color || '#ffffff'}
                onChange={handleFieldChange('color')}
                className={
                  'h-10 w-14 cursor-pointer rounded-md border border-gray-300 p-0.5 ' +
                  'dark:border-gray-600'
                }
                aria-describedby="app-color-help"
              />
              <input
                type="text"
                value={formState.color}
                onChange={handleFieldChange('color')}
                className={INPUT_CLASS + ' max-w-[8rem]'}
                aria-label="Color hex value"
                placeholder="#ffffff"
              />
            </div>
            <p
              id="app-color-help"
              className="mt-1 text-xs text-gray-500 dark:text-gray-400"
            >
              Theme color associated with the application.
            </p>
          </div>

          {/* ---- Author field ---- */}
          <div>
            <label htmlFor="app-author" className={LABEL_CLASS}>
              Author
            </label>
            <input
              id="app-author"
              name="author"
              type="text"
              autoComplete="off"
              value={formState.author}
              onChange={handleFieldChange('author')}
              className={INPUT_CLASS}
              aria-describedby="app-author-help"
            />
            <p
              id="app-author-help"
              className="mt-1 text-xs text-gray-500 dark:text-gray-400"
            >
              Author or team responsible for the application.
            </p>
          </div>

          {/* ---- Weight field (integer) ---- */}
          <div>
            <label htmlFor="app-weight" className={LABEL_CLASS}>
              Weight
            </label>
            <input
              id="app-weight"
              name="weight"
              type="number"
              step="1"
              min="0"
              value={formState.weight}
              onChange={handleFieldChange('weight')}
              className={INPUT_CLASS + ' max-w-[10rem]'}
              aria-describedby="app-weight-help"
            />
            <p
              id="app-weight-help"
              className="mt-1 text-xs text-gray-500 dark:text-gray-400"
            >
              Sort order weight (lower numbers appear first).
            </p>
          </div>

          {/* ---- Description field (textarea) ---- */}
          <div>
            <label htmlFor="app-description" className={LABEL_CLASS}>
              Description
            </label>
            <textarea
              id="app-description"
              name="description"
              rows={4}
              value={formState.description}
              onChange={handleFieldChange('description')}
              className={INPUT_CLASS + ' resize-y'}
              aria-describedby="app-description-help"
            />
            <p
              id="app-description-help"
              className="mt-1 text-xs text-gray-500 dark:text-gray-400"
            >
              Optional description for the application.
            </p>
          </div>

          {/* ---- Access field (multiselect with role options) ---- */}
          <div>
            <label htmlFor="app-access" className={LABEL_CLASS}>
              Access
            </label>
            <select
              id="app-access"
              name="access"
              multiple
              size={Math.min(roles.length || 3, 8)}
              value={formState.access}
              onChange={handleAccessChange}
              className={INPUT_CLASS + ' min-h-[5rem]'}
              aria-describedby="app-access-help"
            >
              {roles
                .slice()
                .sort((a: ErpRole, b: ErpRole) =>
                  a.name.localeCompare(b.name),
                )
                .map((role: ErpRole) => (
                  <option key={role.id} value={role.id}>
                    {role.name}
                  </option>
                ))}
            </select>
            <p
              id="app-access-help"
              className="mt-1 text-xs text-gray-500 dark:text-gray-400"
            >
              Select which roles can access this application. Hold Ctrl/Cmd to
              select multiple.
            </p>
          </div>
        </div>

        {/* ---- Form actions ---- */}
        <div className="mt-8 flex items-center justify-end gap-3 border-t border-gray-200 pt-6 dark:border-gray-700">
          <Link
            to={appId ? `/admin/applications/${appId}` : '/admin/applications'}
            className={
              'inline-flex items-center rounded-md border border-gray-300 bg-white ' +
              'px-4 py-2 text-sm font-medium text-gray-700 shadow-sm ' +
              'hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 ' +
              'focus-visible:ring-indigo-500 focus-visible:ring-offset-2 ' +
              'dark:border-gray-600 dark:bg-gray-800 dark:text-gray-300 ' +
              'dark:hover:bg-gray-700'
            }
          >
            Cancel
          </Link>
          <button
            type="submit"
            disabled={isPending}
            className={
              'inline-flex items-center rounded-md border border-transparent bg-indigo-600 ' +
              'px-4 py-2 text-sm font-medium text-white shadow-sm ' +
              'hover:bg-indigo-700 focus-visible:outline-none focus-visible:ring-2 ' +
              'focus-visible:ring-indigo-500 focus-visible:ring-offset-2 ' +
              'disabled:cursor-not-allowed disabled:opacity-50 ' +
              'dark:bg-indigo-500 dark:hover:bg-indigo-600'
            }
          >
            {isPending ? (
              <>
                <svg
                  className="-ml-0.5 mr-2 h-4 w-4 animate-spin"
                  xmlns="http://www.w3.org/2000/svg"
                  fill="none"
                  viewBox="0 0 24 24"
                  aria-hidden="true"
                >
                  <circle
                    className="opacity-25"
                    cx="12"
                    cy="12"
                    r="10"
                    stroke="currentColor"
                    strokeWidth="4"
                  />
                  <path
                    className="opacity-75"
                    fill="currentColor"
                    d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
                  />
                </svg>
                Saving…
              </>
            ) : (
              'Save Application'
            )}
          </button>
        </div>
      </DynamicForm>
    </section>
  );
}

export default ApplicationManage;
