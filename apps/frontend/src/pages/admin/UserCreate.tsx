/**
 * UserCreate page component — Create new system user with role assignment
 *
 * Route: /admin/users/create
 * Replaces: WebVella.Erp.Plugins.SDK/Pages/user/create.cshtml[.cs]
 *
 * Provides a form for creating new system users with:
 * - Required fields: Email, Username, Password
 * - Optional fields: Image (URL with 80×80 preview), FirstName, LastName
 * - Defaults: Enabled=true, Verified=true
 * - Role multiselect: excludes guest role, defaults to regular role, sorted alphabetically
 * - On success: navigates to /admin/users
 * - On error: displays validation errors from Identity API
 */

import { useState, useCallback, type FormEvent } from 'react';
import { useNavigate, Link } from 'react-router-dom';
import { useCreateUser, useRoles } from '../../hooks/useUsers';
import type { ErpRole } from '../../types/user';
import DynamicForm, { type FormValidation } from '../../components/forms/DynamicForm';

/** Preview size for user avatar image in pixels (matches 80×80 from source) */
const IMAGE_PREVIEW_SIZE = 80;

/**
 * User creation page component.
 *
 * Renders a two-column form matching the original Razor Page layout with
 * Email, Username, Password, Image (URL + 80×80 preview), Roles multiselect,
 * FirstName, LastName, Enabled, and Verified fields.
 *
 * Submits via `POST /v1/identity/users` through the `useCreateUser` TanStack
 * Query mutation. On success the user is redirected to `/admin/users`.
 */
export default function UserCreate() {
  const navigate = useNavigate();

  /* ------------------------------------------------------------------ */
  /*  TanStack Query hooks                                               */
  /* ------------------------------------------------------------------ */
  const { mutate, isPending, isError, error } = useCreateUser();
  const { data: roles, isLoading: rolesLoading } = useRoles();

  /* ------------------------------------------------------------------ */
  /*  Controlled form state (matching create.cshtml.cs BindProperty)     */
  /* ------------------------------------------------------------------ */
  const [email, setEmail] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [image, setImage] = useState('');
  const [firstName, setFirstName] = useState('');
  const [lastName, setLastName] = useState('');
  const [enabled, setEnabled] = useState(true);
  const [showSuccess, setShowSuccess] = useState(false);
  const [verified, setVerified] = useState(true);

  /**
   * Role selection state.
   * `null` means the user has not yet interacted with the role checkboxes,
   * so the "regular" role is shown as pre-selected (matching monolith default).
   * Once the user toggles any checkbox the state becomes an explicit array.
   */
  const [selectedRoles, setSelectedRoles] = useState<string[] | null>(null);

  /* ------------------------------------------------------------------ */
  /*  Validation state for DynamicForm                                   */
  /* ------------------------------------------------------------------ */
  const [validation, setValidation] = useState<FormValidation | undefined>(
    undefined,
  );

  /* ------------------------------------------------------------------ */
  /*  Derived role data                                                  */
  /* ------------------------------------------------------------------ */

  /**
   * Raw role array unwrapped from the ApiResponse envelope.
   * `useRoles()` returns `useQuery<ApiResponse<ErpRole[]>>`, so `data`
   * is the full `ApiResponse<ErpRole[]>` and the array lives in `.object`.
   */
  const rawRoles: ErpRole[] = roles?.object ?? [];

  /** Available roles: guest filtered out, sorted alphabetically by name. */
  const availableRoles: ErpRole[] = rawRoles
    .filter((role: ErpRole) => role.name.toLowerCase() !== 'guest')
    .sort((a: ErpRole, b: ErpRole) => a.name.localeCompare(b.name));

  /** ID of the "regular" role used as the default selection. */
  const regularRoleId = availableRoles.find(
    (r: ErpRole) => r.name.toLowerCase() === 'regular',
  )?.id;

  /**
   * Effective selected roles for display and submission.
   * Defaults to `[regularRoleId]` when the user hasn't yet interacted.
   */
  const effectiveSelectedRoles: string[] =
    selectedRoles !== null
      ? selectedRoles
      : regularRoleId
        ? [regularRoleId]
        : [];

  /* ------------------------------------------------------------------ */
  /*  Handlers                                                           */
  /* ------------------------------------------------------------------ */

  /** Toggle a role checkbox. Materialises defaults on first interaction. */
  const handleRoleToggle = useCallback(
    (roleId: string) => {
      setSelectedRoles((prev) => {
        const current =
          prev !== null ? prev : regularRoleId ? [regularRoleId] : [];
        return current.includes(roleId)
          ? current.filter((id) => id !== roleId)
          : [...current, roleId];
      });
    },
    [regularRoleId],
  );

  /** Validate form, build payload, fire mutation, and handle result. */
  const handleSubmit = useCallback(
    (e: FormEvent<HTMLFormElement>) => {
      e.preventDefault();
      setValidation(undefined);

      /* ---------- client-side required-field validation ---------- */
      const errors: Array<{ propertyName: string; message: string }> = [];
      if (!email.trim()) {
        errors.push({ propertyName: 'email', message: 'Email is required.' });
      }
      // Username defaults to the local part of email (before @) if left blank,
      // avoiding duplicate text in the user table when email and username cells
      // both show the full email address.
      const effectiveUsername = username.trim() || email.trim().split('@')[0];
      if (!password.trim()) {
        errors.push({
          propertyName: 'password',
          message: 'Password is required.',
        });
      }

      if (errors.length > 0) {
        setValidation({ message: 'Please fix the following errors:', errors });
        return;
      }

      /* ---------- build payload ---------- */
      const rolesForPayload =
        selectedRoles !== null
          ? selectedRoles
          : regularRoleId
            ? [regularRoleId]
            : [];

      const payload = {
        email: email.trim(),
        username: effectiveUsername,
        password,
        firstName: firstName.trim(),
        lastName: lastName.trim(),
        image: image.trim() || undefined,
        roleIds: rolesForPayload.length > 0 ? rolesForPayload : undefined,
        enabled,
        verified,
      };

      mutate(payload, {
        onSuccess: () => {
          setShowSuccess(true);
          setTimeout(() => navigate('/admin/users'), 1500);
        },
        onError: (err: unknown) => {
          const apiError = err as {
            message?: string;
            errors?: Array<{ key: string; value: string; message: string }>;
          };

          if (apiError.errors && apiError.errors.length > 0) {
            setValidation({
              message:
                apiError.message ||
                'An error occurred while creating the user.',
              errors: apiError.errors.map((item) => ({
                propertyName: item.key || '',
                message: item.message,
              })),
            });
          } else {
            setValidation({
              message:
                apiError.message ||
                'An error occurred while creating the user.',
              errors: [],
            });
          }
        },
      });
    },
    [
      email,
      username,
      password,
      firstName,
      lastName,
      image,
      selectedRoles,
      regularRoleId,
      enabled,
      verified,
      mutate,
      navigate,
    ],
  );

  /* ------------------------------------------------------------------ */
  /*  Render                                                             */
  /* ------------------------------------------------------------------ */
  return (
    <div className="mx-auto max-w-4xl">
      {showSuccess && (
        <div className="mb-4 rounded-md bg-green-50 p-4" role="status" aria-live="polite">
          <p className="text-sm font-medium text-green-800" data-testid="success-notification">User created successfully. Redirecting…</p>
        </div>
      )}
      {/* Page header */}
      <div className="mb-6 flex items-center justify-between">
        <h1 className="text-2xl font-semibold text-gray-900">Create User</h1>
        <Link
          to="/admin/users"
          className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
        >
          Cancel
        </Link>
      </div>

      {/* Fallback error banner when the mutation error was not mapped to validation */}
      {isError && error && !validation && (
        <div
          role="alert"
          className="mb-4 rounded-md border border-red-200 bg-red-50 p-4"
        >
          <p className="text-sm text-red-800">
            {(error as { message?: string }).message ||
              'An unexpected error occurred while creating the user.'}
          </p>
        </div>
      )}

      {/* User creation form */}
      <DynamicForm
        id="CreateRecord"
        name="CreateRecord"
        labelMode="stacked"
        fieldMode="form"
        showValidation={isError || validation !== undefined}
        validation={validation}
        onSubmit={handleSubmit}
        className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm"
      >
        {/* Row 1: Email + Image (80×80 preview) */}
        <div className="mb-6 grid grid-cols-1 gap-6 sm:grid-cols-12">
          <div className="sm:col-span-6">
            <label
              htmlFor="user-email"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Email{' '}
              <span className="text-red-500" aria-hidden="true">
                *
              </span>
            </label>
            <input
              id="user-email"
              type="email"
              name="email"
              required
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus-visible:border-indigo-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-0 focus-visible:outline-indigo-500"
              placeholder="user@example.com"
              autoComplete="email"
            />
          </div>

          <div className="sm:col-span-3">
            <label
              htmlFor="user-image"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Image
            </label>
            <div className="flex items-start gap-3">
              {image ? (
                <img
                  src={image}
                  alt="User avatar preview"
                  width={IMAGE_PREVIEW_SIZE}
                  height={IMAGE_PREVIEW_SIZE}
                  className="rounded border border-gray-200 bg-gray-100 object-cover"
                  style={{
                    width: IMAGE_PREVIEW_SIZE,
                    height: IMAGE_PREVIEW_SIZE,
                  }}
                />
              ) : (
                <div
                  className="flex items-center justify-center rounded border border-dashed border-gray-300 bg-gray-50 text-xs text-gray-400"
                  style={{
                    width: IMAGE_PREVIEW_SIZE,
                    height: IMAGE_PREVIEW_SIZE,
                  }}
                  aria-hidden="true"
                >
                  No image
                </div>
              )}
            </div>
            <input
              id="user-image"
              type="url"
              name="image"
              value={image}
              onChange={(e) => setImage(e.target.value)}
              className="mt-2 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus-visible:border-indigo-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-0 focus-visible:outline-indigo-500"
              placeholder="https://example.com/avatar.jpg"
            />
          </div>
        </div>

        {/* Row 2: Username */}
        <div className="mb-6 grid grid-cols-1 gap-6 sm:grid-cols-12">
          <div className="sm:col-span-6">
            <label
              htmlFor="user-username"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Username{' '}
              <span className="text-red-500" aria-hidden="true">
                *
              </span>
            </label>
            <input
              id="user-username"
              type="text"
              name="username"
              required
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus-visible:border-indigo-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-0 focus-visible:outline-indigo-500"
              placeholder="username"
              autoComplete="username"
            />
          </div>
        </div>

        {/* Row 3: Password + Roles multiselect */}
        <div className="mb-6 grid grid-cols-1 gap-6 sm:grid-cols-12">
          <div className="sm:col-span-6">
            <label
              htmlFor="user-password"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Password{' '}
              <span className="text-red-500" aria-hidden="true">
                *
              </span>
            </label>
            <input
              id="user-password"
              type="password"
              name="password"
              required
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus-visible:border-indigo-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-0 focus-visible:outline-indigo-500"
              placeholder="••••••••"
              autoComplete="new-password"
            />
          </div>

          <div className="sm:col-span-6">
            <fieldset>
              <legend className="mb-1 block text-sm font-medium text-gray-700">
                Roles
              </legend>

              {rolesLoading && (
                <p className="text-sm text-gray-500" aria-live="polite">
                  Loading roles…
                </p>
              )}

              {!rolesLoading && availableRoles.length === 0 && (
                <p className="text-sm text-gray-500">No roles available.</p>
              )}

              {!rolesLoading && availableRoles.length > 0 && (
                <div className="max-h-40 space-y-2 overflow-y-auto rounded-md border border-gray-300 p-3">
                  {availableRoles.map((role: ErpRole) => (
                    <label
                      key={role.id}
                      className="flex cursor-pointer items-center gap-2 text-sm text-gray-700"
                    >
                      <input
                        type="checkbox"
                        value={role.id}
                        checked={effectiveSelectedRoles.includes(role.id)}
                        onChange={() => handleRoleToggle(role.id)}
                        className="h-4 w-4 rounded border-gray-300 text-indigo-600 focus-visible:ring-indigo-500"
                      />
                      <span>{role.name}</span>
                    </label>
                  ))}
                </div>
              )}
            </fieldset>
          </div>
        </div>

        {/* Row 4: First Name + Last Name */}
        <div className="mb-6 grid grid-cols-1 gap-6 sm:grid-cols-12">
          <div className="sm:col-span-6">
            <label
              htmlFor="user-firstName"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              First Name
            </label>
            <input
              id="user-firstName"
              type="text"
              name="firstName"
              value={firstName}
              onChange={(e) => setFirstName(e.target.value)}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus-visible:border-indigo-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-0 focus-visible:outline-indigo-500"
              placeholder="First name"
              autoComplete="given-name"
            />
          </div>

          <div className="sm:col-span-6">
            <label
              htmlFor="user-lastName"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Last Name
            </label>
            <input
              id="user-lastName"
              type="text"
              name="lastName"
              value={lastName}
              onChange={(e) => setLastName(e.target.value)}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus-visible:border-indigo-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-0 focus-visible:outline-indigo-500"
              placeholder="Last name"
              autoComplete="family-name"
            />
          </div>
        </div>

        {/* Row 5: Enabled + Verified checkboxes */}
        <div className="mb-6 grid grid-cols-1 gap-6 sm:grid-cols-12">
          <div className="sm:col-span-6">
            <label className="flex cursor-pointer items-center gap-2">
              <input
                type="checkbox"
                name="enabled"
                checked={enabled}
                onChange={(e) => setEnabled(e.target.checked)}
                className="h-4 w-4 rounded border-gray-300 text-indigo-600 focus-visible:ring-indigo-500"
              />
              <span className="text-sm font-medium text-gray-700">
                Enabled
              </span>
            </label>
          </div>

          <div className="sm:col-span-6">
            <label className="flex cursor-pointer items-center gap-2">
              <input
                type="checkbox"
                name="verified"
                checked={verified}
                onChange={(e) => setVerified(e.target.checked)}
                className="h-4 w-4 rounded border-gray-300 text-indigo-600 focus-visible:ring-indigo-500"
              />
              <span className="text-sm font-medium text-gray-700">
                Verified
              </span>
            </label>
          </div>
        </div>

        {/* Form actions */}
        <div className="flex items-center justify-between border-t border-gray-200 pt-4">
          <Link
            to="/admin/users"
            className="text-sm font-medium text-gray-600 hover:text-gray-900"
          >
            ← Back to Users
          </Link>
          <button
            type="submit"
            disabled={isPending}
            className="inline-flex items-center rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isPending ? 'Creating…' : 'Create User'}
          </button>
        </div>
      </DynamicForm>
    </div>
  );
}
