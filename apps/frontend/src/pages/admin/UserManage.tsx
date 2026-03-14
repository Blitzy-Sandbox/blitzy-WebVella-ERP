import { useState, useEffect, useCallback } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { useUser, useUpdateUser, useRoles } from '../../hooks/useUsers';
import type { ErpUser, ErpRole } from '../../types/user';
import DynamicForm from '../../components/forms/DynamicForm';

/**
 * Extended user data shape that may be returned by the Identity service API.
 * The base ErpUser type represents the public-facing user profile; the management
 * endpoint additionally returns enabled, verified, and roleIds for admin editing.
 */
interface UserManageData extends ErpUser {
  enabled?: boolean;
  verified?: boolean;
  roleIds?: string[];
}

/**
 * UserManage — Admin user edit page.
 *
 * Route: /admin/users/:userId/manage
 *
 * Replaces the monolith's WebVella.Erp.Plugins.SDK/Pages/user/manage.cshtml[.cs].
 * Fetches a single user by ID, pre-populates a form with their current data,
 * and submits updates via the Identity service PUT /v1/identity/users/:id endpoint.
 *
 * Form fields: Email (required), Username (required), Password (optional — leave
 * blank to keep current), Image (URL with 80×80 preview), FirstName, LastName,
 * Enabled (checkbox), Verified (checkbox), Roles (multiselect excluding "guest",
 * sorted alphabetically by name).
 */
function UserManage(): React.JSX.Element {
  // ---------------------------------------------------------------------------
  // Routing
  // ---------------------------------------------------------------------------
  const { userId } = useParams<{ userId: string }>();
  const navigate = useNavigate();

  // ---------------------------------------------------------------------------
  // Server-state hooks
  // ---------------------------------------------------------------------------
  const {
    data: userData,
    isLoading: isUserLoading,
    isError: isUserError,
  } = useUser(userId);

  const {
    mutate: updateUser,
    isPending: isUpdatePending,
    isError: isUpdateError,
  } = useUpdateUser();

  const { data: rolesData } = useRoles();

  // ---------------------------------------------------------------------------
  // Local form state — 9 controlled fields matching manage.cshtml.cs BindProperties
  // ---------------------------------------------------------------------------
  const [email, setEmail] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [showSuccess, setShowSuccess] = useState(false);
  const [image, setImage] = useState('');
  const [firstName, setFirstName] = useState('');
  const [lastName, setLastName] = useState('');
  const [enabled, setEnabled] = useState(true);
  const [verified, setVerified] = useState(true);
  const [selectedRoles, setSelectedRoles] = useState<string[]>([]);

  /** Inline validation / mutation error message */
  const [formError, setFormError] = useState('');

  // ---------------------------------------------------------------------------
  // Pre-populate form when user data loads (mirrors manage.cshtml.cs OnGet)
  // ---------------------------------------------------------------------------
  useEffect(() => {
    if (userData?.object) {
      const user = userData.object as UserManageData;
      setEmail(user.email ?? '');
      setUsername(user.username ?? '');
      setImage(user.image ?? '');
      setFirstName(user.firstName ?? '');
      setLastName(user.lastName ?? '');
      setEnabled(user.enabled ?? true);
      setVerified(user.verified ?? true);
      setSelectedRoles(user.roleIds ?? []);
      // Password is intentionally never pre-populated for security reasons
      setPassword('');
    }
  }, [userData]);

  // ---------------------------------------------------------------------------
  // Derived: available roles (exclude "guest", sort alphabetically by name)
  // Mirrors manage.cshtml.cs InitPage() role processing:
  //   allRoles.RemoveAll(x => x.Name == "guest");
  //   allRoles = allRoles.OrderBy(x => x.Name).ToList();
  // ---------------------------------------------------------------------------
  const availableRoles: ErpRole[] = (() => {
    const roles = rolesData?.object;
    if (!Array.isArray(roles)) return [];
    return roles
      .filter((role: ErpRole) => role.name.toLowerCase() !== 'guest')
      .sort((a: ErpRole, b: ErpRole) => a.name.localeCompare(b.name));
  })();

  // ---------------------------------------------------------------------------
  // Role checkbox toggle handler
  // ---------------------------------------------------------------------------
  const handleRoleToggle = useCallback((roleId: string) => {
    setSelectedRoles((prev) =>
      prev.includes(roleId)
        ? prev.filter((id) => id !== roleId)
        : [...prev, roleId],
    );
  }, []);

  // ---------------------------------------------------------------------------
  // Form submission — mirrors manage.cshtml.cs OnPost
  // ---------------------------------------------------------------------------
  const handleSubmit = useCallback(
    (e?: React.FormEvent<HTMLFormElement>) => {
      if (e) e.preventDefault();
      setFormError('');

      if (!userId) return;

      // Client-side required-field validation
      if (!email.trim()) {
        setFormError('Email is required.');
        return;
      }
      if (!username.trim()) {
        setFormError('Username is required.');
        return;
      }

      // Construct UpdateUserPayload; omit password if blank (keep current)
      const payload: {
        id: string;
        email: string;
        username: string;
        password?: string;
        image?: string;
        firstName?: string;
        lastName?: string;
        enabled: boolean;
        verified: boolean;
        roleIds: string[];
      } = {
        id: userId,
        email: email.trim(),
        username: username.trim(),
        enabled,
        verified,
        roleIds: selectedRoles,
      };

      if (password.trim()) {
        payload.password = password.trim();
      }
      if (image.trim()) {
        payload.image = image.trim();
      }
      if (firstName.trim()) {
        payload.firstName = firstName.trim();
      }
      if (lastName.trim()) {
        payload.lastName = lastName.trim();
      }

      updateUser(payload, {
        onSuccess: () => {
          setShowSuccess(true);
          setTimeout(() => navigate('/admin/users'), 1500);
        },
        onError: (error: unknown) => {
          const message =
            error instanceof Error
              ? error.message
              : 'An error occurred while updating the user.';
          setFormError(message);
        },
      });
    },
    [
      userId,
      email,
      username,
      password,
      image,
      firstName,
      lastName,
      enabled,
      verified,
      selectedRoles,
      updateUser,
      navigate,
    ],
  );

  // ---------------------------------------------------------------------------
  // Loading state
  // ---------------------------------------------------------------------------
  // Show success notification immediately, even during loading transitions
  if (showSuccess) {
    return (
      <div className="p-6">
        <div className="mb-4 rounded-md bg-green-50 p-4" role="status" aria-live="polite">
          <p className="text-sm font-medium text-green-800" data-testid="success-notification">User saved successfully. Redirecting…</p>
        </div>
      </div>
    );
  }

  if (isUserLoading) {
    return (
      <div className="flex items-center justify-center py-16" role="status">
        <div className="flex flex-col items-center gap-3">
          <span
            className="inline-block h-8 w-8 animate-spin rounded-full border-4 border-blue-600 border-t-transparent"
            aria-hidden="true"
          />
          <span className="text-sm text-gray-500">Loading user data…</span>
        </div>
      </div>
    );
  }

  // ---------------------------------------------------------------------------
  // Error / not-found state (mirrors manage.cshtml.cs NotFound())
  // ---------------------------------------------------------------------------
  if (isUserError || !userData?.object) {
    return (
      <div className="flex items-center justify-center py-16">
        <div className="rounded-lg border border-red-200 bg-red-50 px-8 py-6 text-center">
          <h2 className="mb-2 text-lg font-semibold text-red-700">
            User Not Found
          </h2>
          <p className="mb-4 text-sm text-red-600">
            The requested user could not be found or an error occurred while
            loading.
          </p>
          <Link
            to="/admin/users"
            className="inline-flex items-center gap-1.5 rounded-md bg-gray-100 px-4 py-2 text-sm font-medium text-gray-700 transition-colors hover:bg-gray-200 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400"
          >
            ← Back to Users
          </Link>
        </div>
      </div>
    );
  }

  const user = userData.object;

  // ---------------------------------------------------------------------------
  // Render
  // ---------------------------------------------------------------------------
  return (
    <div className="mx-auto max-w-5xl px-4 py-6">
      {/* ------------------------------------------------------------------ */}
      {/* Page Header                                                        */}
      {/* ------------------------------------------------------------------ */}
      <div className="mb-6 flex flex-wrap items-center justify-between gap-4">
        <div className="flex items-center gap-3">
          <div
            className="flex h-10 w-10 items-center justify-center rounded-lg"
            style={{ backgroundColor: '#dc3545' }}
            aria-hidden="true"
          >
            <i className="fa fa-user text-white" />
          </div>
          <h1 className="text-xl font-semibold text-gray-900">Manage User</h1>
        </div>

        <div className="flex items-center gap-2">
          <button
            type="button"
            onClick={() => handleSubmit()}
            disabled={isUpdatePending}
            className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isUpdatePending ? (
              <>
                <span
                  className="inline-block h-4 w-4 animate-spin rounded-full border-2 border-current border-t-transparent"
                  aria-hidden="true"
                />
                Saving…
              </>
            ) : (
              <>
                <i className="fa fa-save" aria-hidden="true" />
                Save User
              </>
            )}
          </button>

          <Link
            to="/admin/users"
            className="inline-flex items-center gap-1.5 rounded-md bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 transition-colors hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400"
          >
            Cancel
          </Link>
        </div>
      </div>

      {/* ------------------------------------------------------------------ */}
      {/* Validation / Mutation Error Banner                                 */}
      {/* ------------------------------------------------------------------ */}
      {(isUpdateError || formError) && (
        <div
          className="mb-6 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700"
          role="alert"
        >
          <p className="font-medium">
            {formError ||
              'An error occurred while saving the user. Please try again.'}
          </p>
        </div>
      )}

      {/* ------------------------------------------------------------------ */}
      {/* User Edit Form                                                     */}
      {/* Replaces: <wv-form id="ManageRecord" name="ManageRecord"           */}
      {/*            label-mode="Stacked" mode="Form">                       */}
      {/* ------------------------------------------------------------------ */}
      <DynamicForm
        id="ManageRecord"
        name="ManageRecord"
        labelMode="stacked"
        fieldMode="form"
        onSubmit={handleSubmit}
      >
        <div className="grid grid-cols-1 gap-x-6 gap-y-5 md:grid-cols-2">
          {/* -------------------------------------------------------------- */}
          {/* Row 1 — Email (required) + Username (required)                 */}
          {/* -------------------------------------------------------------- */}
          <div>
            <label
              htmlFor="field-email"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Email <span className="text-red-500" aria-label="required">*</span>
            </label>
            <input
              id="field-email"
                name="email"
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              required
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              placeholder="user@example.com"
              autoComplete="email"
            />
          </div>

          <div>
            <label
              htmlFor="field-username"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Username{' '}
              <span className="text-red-500" aria-label="required">*</span>
            </label>
            <input
              id="field-username"
              type="text"
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              required
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              placeholder="Enter username"
              autoComplete="username"
            />
          </div>

          {/* -------------------------------------------------------------- */}
          {/* Row 2 — Password + Image (80×80 preview)                       */}
          {/* -------------------------------------------------------------- */}
          <div>
            <label
              htmlFor="field-password"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Password
            </label>
            <input
              id="field-password"
                name="password"
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              placeholder="Leave blank to keep current"
              autoComplete="new-password"
            />
            <p className="mt-1 text-xs text-gray-500">
              Leave blank to keep the current password unchanged.
            </p>
          </div>

          <div>
            <label
              htmlFor="field-image"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Image
            </label>
            <div className="flex items-start gap-3">
              {image ? (
                <img
                  src={image}
                  alt={`${user.username ?? 'User'} avatar`}
                  width={80}
                  height={80}
                  className="h-20 w-20 flex-shrink-0 rounded-md border border-gray-200 bg-gray-100 object-cover"
                  loading="lazy"
                  decoding="async"
                />
              ) : (
                <div
                  className="flex h-20 w-20 flex-shrink-0 items-center justify-center rounded-md border border-gray-200 bg-gray-100"
                  aria-hidden="true"
                >
                  <i className="fa fa-user text-2xl text-gray-400" />
                </div>
              )}
              <input
                id="field-image"
                type="text"
                value={image}
                onChange={(e) => setImage(e.target.value)}
                className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                placeholder="https://example.com/avatar.png"
              />
            </div>
          </div>

          {/* -------------------------------------------------------------- */}
          {/* Row 3 — Roles multiselect (full-width)                         */}
          {/* Excludes guest role, sorted alphabetically.                    */}
          {/* -------------------------------------------------------------- */}
          <fieldset className="md:col-span-2">
            <legend className="mb-1 block text-sm font-medium text-gray-700">
              Roles
            </legend>
            {availableRoles.length === 0 ? (
              <p className="text-sm text-gray-500">Loading roles…</p>
            ) : (
              <div className="flex flex-wrap gap-2 rounded-md border border-gray-300 bg-white p-3">
                {availableRoles.map((role: ErpRole) => (
                  <label
                    key={role.id}
                    className="inline-flex cursor-pointer items-center gap-1.5 rounded-full border border-gray-200 bg-gray-50 px-3 py-1.5 text-sm transition-colors hover:bg-gray-100"
                  >
                    <input
                      type="checkbox"
                      checked={selectedRoles.includes(role.id)}
                      onChange={() => handleRoleToggle(role.id)}
                      className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                    />
                    <span className="select-none">{role.name}</span>
                  </label>
                ))}
              </div>
            )}
          </fieldset>

          {/* -------------------------------------------------------------- */}
          {/* Row 4 — First Name + Last Name                                 */}
          {/* -------------------------------------------------------------- */}
          <div>
            <label
              htmlFor="field-firstName"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              First Name
            </label>
            <input
              id="field-firstName"
                name="firstName"
              type="text"
              value={firstName}
              onChange={(e) => setFirstName(e.target.value)}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              placeholder="Enter first name"
              autoComplete="given-name"
            />
          </div>

          <div>
            <label
              htmlFor="field-lastName"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Last Name
            </label>
            <input
              id="field-lastName"
                name="lastName"
              type="text"
              value={lastName}
              onChange={(e) => setLastName(e.target.value)}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              placeholder="Enter last name"
              autoComplete="family-name"
            />
          </div>

          {/* -------------------------------------------------------------- */}
          {/* Row 5 — Enabled + Verified checkboxes                          */}
          {/* -------------------------------------------------------------- */}
          <div>
            <div className="flex items-center gap-2">
              <input
                id="field-enabled"
                type="checkbox"
                checked={enabled}
                onChange={(e) => setEnabled(e.target.checked)}
                className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
              />
              <label
                htmlFor="field-enabled"
                className="text-sm font-medium text-gray-700"
              >
                Enabled
              </label>
            </div>
            <p className="mt-1 text-xs text-gray-500">
              Whether the user account is active and can log in.
            </p>
          </div>

          <div>
            <div className="flex items-center gap-2">
              <input
                id="field-verified"
                type="checkbox"
                checked={verified}
                onChange={(e) => setVerified(e.target.checked)}
                className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
              />
              <label
                htmlFor="field-verified"
                className="text-sm font-medium text-gray-700"
              >
                Verified
              </label>
            </div>
            <p className="mt-1 text-xs text-gray-500">
              Whether the user's email address has been verified.
            </p>
          </div>
        </div>
      </DynamicForm>
    </div>
  );
}

export default UserManage;
