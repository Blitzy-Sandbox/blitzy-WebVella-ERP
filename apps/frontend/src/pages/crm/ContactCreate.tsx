import { useState, useCallback, useMemo } from 'react';
import type { FormEvent, ChangeEvent, DragEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { useMutation, useQuery } from '@tanstack/react-query';
import apiClient from '../../api/client';
import { useFileUpload } from '../../hooks/useFiles';
import type { EntityRecord } from '../../types/record';

/* ------------------------------------------------------------------ */
/*  Local type definitions                                             */
/* ------------------------------------------------------------------ */

/** Strongly-typed form state for every editable contact field. */
interface ContactFormState {
  salutation_id: string;
  first_name: string;
  last_name: string;
  job_title: string;
  email: string;
  fixed_phone: string;
  mobile_phone: string;
  fax_phone: string;
  street: string;
  street_2: string;
  city: string;
  region: string;
  post_code: string;
  country_id: string;
  photo: string;
  notes: string;
}

/** Mapping of field name to its client-side validation error message. */
interface ValidationErrors {
  [key: string]: string;
}

/** Shape for the salutation / country dropdown options. */
interface SelectOption {
  value: string;
  label: string;
}

/* ------------------------------------------------------------------ */
/*  Constants                                                          */
/* ------------------------------------------------------------------ */

/** Default salutation_id extracted from NextPlugin.20190206 seed data. */
const DEFAULT_SALUTATION_ID = '87c08ee1-8d4d-4c89-9b37-4e3cc3f98698';

/** Lookup queries use a long stale time — salutations and countries are near-static. */
const REFERENCE_DATA_STALE_TIME = 30 * 60 * 1000; // 30 minutes

/** Simple email format check (RFC-822 subset). */
const EMAIL_REGEX = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/** Permissive phone pattern: digits, spaces, dashes, parens, optional leading +. */
const PHONE_REGEX = /^[+]?[\d\s\-().]{3,30}$/;

/** Allowed MIME types for the contact photo upload. */
const ALLOWED_IMAGE_TYPES = new Set([
  'image/jpeg',
  'image/png',
  'image/gif',
  'image/webp',
]);

/** Initial blank form state with the seeded default salutation selected. */
const INITIAL_FORM_STATE: ContactFormState = {
  salutation_id: DEFAULT_SALUTATION_ID,
  first_name: '',
  last_name: '',
  job_title: '',
  email: '',
  fixed_phone: '',
  mobile_phone: '',
  fax_phone: '',
  street: '',
  street_2: '',
  city: '',
  region: '',
  post_code: '',
  country_id: '',
  photo: '',
  notes: '',
};

/* ================================================================== */
/*  ContactCreate — CRM Contact Creation Page Component               */
/* ================================================================== */

/**
 * Full-page form for creating a new CRM contact.
 *
 * Replaces the monolith's RecordCreate.cshtml Razor Page for the
 * contact entity.  Fetches salutation and country reference data for
 * dropdown fields and uses the File Management service (via S3
 * presigned URLs) for photo uploads.
 *
 * Route: /crm/contacts/create
 */
export default function ContactCreate() {
  const navigate = useNavigate();

  /* ---------------------------------------------------------------- */
  /*  File-upload hook for the contact photo                          */
  /* ---------------------------------------------------------------- */
  const {
    upload,
    progress,
    isUploading,
    isError: isUploadError,
    error: uploadError,
    reset: resetUpload,
  } = useFileUpload();

  /* ---------------------------------------------------------------- */
  /*  Form state                                                       */
  /* ---------------------------------------------------------------- */
  const [formState, setFormState] = useState<ContactFormState>(INITIAL_FORM_STATE);
  const [errors, setErrors] = useState<ValidationErrors>({});
  const [photoPreview, setPhotoPreview] = useState<string>('');
  const [isDragActive, setIsDragActive] = useState(false);

  /* ---------------------------------------------------------------- */
  /*  Reference-data queries (salutations & countries)                */
  /* ---------------------------------------------------------------- */
  const { data: salutationsResponse, isLoading: isSalutationsLoading } =
    useQuery({
      queryKey: ['crm', 'salutations'],
      queryFn: () => apiClient.get('/v1/crm/salutations'),
      staleTime: REFERENCE_DATA_STALE_TIME,
    });

  const { data: countriesResponse, isLoading: isCountriesLoading } = useQuery({
    queryKey: ['crm', 'countries'],
    queryFn: () => apiClient.get('/v1/crm/countries'),
    staleTime: REFERENCE_DATA_STALE_TIME,
  });

  /* ---------------------------------------------------------------- */
  /*  Create-contact mutation (POST /v1/crm/contacts)                 */
  /* ---------------------------------------------------------------- */
  const createContact = useMutation({
    mutationFn: (data: EntityRecord) =>
      apiClient.post('/v1/crm/contacts', data),
    onSuccess: (response) => {
      const record = response?.data?.object as EntityRecord | undefined;
      if (record?.id) {
        navigate(`/crm/contacts/${String(record.id)}`);
      } else {
        navigate('/crm/contacts');
      }
    },
  });

  /* ---------------------------------------------------------------- */
  /*  Memoised dropdown option lists                                   */
  /* ---------------------------------------------------------------- */
  const salutationOptions = useMemo<SelectOption[]>(() => {
    const list = salutationsResponse?.data?.object;
    if (!Array.isArray(list)) return [];
    return list.map((s: EntityRecord) => ({
      value: String(s.id ?? ''),
      label: String(
        (s as EntityRecord).label ?? (s as EntityRecord).name ?? '',
      ),
    }));
  }, [salutationsResponse]);

  const countryOptions = useMemo<SelectOption[]>(() => {
    const list = countriesResponse?.data?.object;
    if (!Array.isArray(list)) return [];
    return list.map((c: EntityRecord) => ({
      value: String(c.id ?? ''),
      label: String(
        (c as EntityRecord).label ?? (c as EntityRecord).name ?? '',
      ),
    }));
  }, [countriesResponse]);

  /* ---------------------------------------------------------------- */
  /*  Client-side validation                                           */
  /* ---------------------------------------------------------------- */
  const validate = useCallback((): ValidationErrors => {
    const errs: ValidationErrors = {};

    /* At least one name field must be provided */
    if (!formState.first_name.trim() && !formState.last_name.trim()) {
      errs.first_name = 'At least a first name or last name is required.';
      errs.last_name = 'At least a first name or last name is required.';
    }

    /* Email format (when provided) */
    if (formState.email.trim() && !EMAIL_REGEX.test(formState.email.trim())) {
      errs.email = 'Please enter a valid email address.';
    }

    /* Phone format (when provided) */
    if (
      formState.fixed_phone.trim() &&
      !PHONE_REGEX.test(formState.fixed_phone.trim())
    ) {
      errs.fixed_phone = 'Please enter a valid phone number.';
    }
    if (
      formState.mobile_phone.trim() &&
      !PHONE_REGEX.test(formState.mobile_phone.trim())
    ) {
      errs.mobile_phone = 'Please enter a valid phone number.';
    }
    if (
      formState.fax_phone.trim() &&
      !PHONE_REGEX.test(formState.fax_phone.trim())
    ) {
      errs.fax_phone = 'Please enter a valid phone number.';
    }

    return errs;
  }, [formState]);

  /* ---------------------------------------------------------------- */
  /*  Generic input handler (text / select / textarea)                */
  /* ---------------------------------------------------------------- */
  const handleInputChange = useCallback(
    (
      e: ChangeEvent<
        HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement
      >,
    ) => {
      const { name, value } = e.target;
      setFormState((prev) => ({ ...prev, [name]: value }));
      /* Clear field-level error on change */
      setErrors((prev) => {
        if (prev[name]) {
          const next = { ...prev };
          delete next[name];
          return next;
        }
        return prev;
      });
    },
    [],
  );

  /* ---------------------------------------------------------------- */
  /*  Photo upload helpers                                             */
  /* ---------------------------------------------------------------- */

  /** Validate image type, show local preview, and upload via S3. */
  const handlePhotoFile = useCallback(
    async (file: File) => {
      if (!ALLOWED_IMAGE_TYPES.has(file.type)) {
        setErrors((prev) => ({
          ...prev,
          photo: 'Please upload a valid image (JPG, PNG, GIF, or WebP).',
        }));
        return;
      }

      /* Clear previous photo errors */
      setErrors((prev) => {
        if (prev.photo) {
          const next = { ...prev };
          delete next.photo;
          return next;
        }
        return prev;
      });

      /* Show local blob preview immediately */
      const blobUrl = URL.createObjectURL(file);
      setPhotoPreview(blobUrl);

      try {
        const result = await upload({ file });
        const fileUrl = result?.object?.url ?? '';
        setFormState((prev) => ({ ...prev, photo: String(fileUrl) }));
      } catch {
        setErrors((prev) => ({
          ...prev,
          photo: 'Photo upload failed. Please try again.',
        }));
        setPhotoPreview('');
        setFormState((prev) => ({ ...prev, photo: '' }));
      }
    },
    [upload],
  );

  /** Handle photo selection from a file input element. */
  const handlePhotoUpload = useCallback(
    (e: ChangeEvent<HTMLInputElement>) => {
      const file = e.target.files?.[0];
      if (file) {
        handlePhotoFile(file);
      }
    },
    [handlePhotoFile],
  );

  /* ---- Drag-and-drop handlers ---- */

  const handleDragOver = useCallback((e: DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    e.stopPropagation();
    setIsDragActive(true);
  }, []);

  const handleDragLeave = useCallback((e: DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    e.stopPropagation();
    setIsDragActive(false);
  }, []);

  const handleDrop = useCallback(
    (e: DragEvent<HTMLDivElement>) => {
      e.preventDefault();
      e.stopPropagation();
      setIsDragActive(false);

      const file = e.dataTransfer.files?.[0];
      if (file) {
        handlePhotoFile(file);
      }
    },
    [handlePhotoFile],
  );

  /** Remove the current photo and reset upload state. */
  const handleRemovePhoto = useCallback(() => {
    if (photoPreview) {
      URL.revokeObjectURL(photoPreview);
    }
    setPhotoPreview('');
    setFormState((prev) => ({ ...prev, photo: '' }));
    resetUpload();
  }, [photoPreview, resetUpload]);

  /* ---------------------------------------------------------------- */
  /*  Form submission                                                  */
  /* ---------------------------------------------------------------- */
  const handleSubmit = useCallback(
    (e: FormEvent<HTMLFormElement>) => {
      e.preventDefault();

      const validationErrors = validate();
      if (Object.keys(validationErrors).length > 0) {
        setErrors(validationErrors);
        return;
      }

      /* Build the EntityRecord payload — only send non-empty fields. */
      const payload: EntityRecord = {};
      if (formState.salutation_id)
        payload.salutation_id = formState.salutation_id;
      if (formState.first_name.trim())
        payload.first_name = formState.first_name.trim();
      if (formState.last_name.trim())
        payload.last_name = formState.last_name.trim();
      if (formState.job_title.trim())
        payload.job_title = formState.job_title.trim();
      if (formState.email.trim()) payload.email = formState.email.trim();
      if (formState.fixed_phone.trim())
        payload.fixed_phone = formState.fixed_phone.trim();
      if (formState.mobile_phone.trim())
        payload.mobile_phone = formState.mobile_phone.trim();
      if (formState.fax_phone.trim())
        payload.fax_phone = formState.fax_phone.trim();
      if (formState.street.trim()) payload.street = formState.street.trim();
      if (formState.street_2.trim())
        payload.street_2 = formState.street_2.trim();
      if (formState.city.trim()) payload.city = formState.city.trim();
      if (formState.region.trim()) payload.region = formState.region.trim();
      if (formState.post_code.trim())
        payload.post_code = formState.post_code.trim();
      if (formState.country_id) payload.country_id = formState.country_id;
      if (formState.photo) payload.photo = formState.photo;
      if (formState.notes.trim()) payload.notes = formState.notes.trim();

      createContact.mutate(payload);
    },
    [formState, validate, createContact],
  );

  /* ---------------------------------------------------------------- */
  /*  Derived / computed values                                        */
  /* ---------------------------------------------------------------- */

  /** Server-side error messages extracted from the mutation rejection. */
  const serverErrors = useMemo<string[]>(() => {
    if (!createContact.isError || !createContact.error) return [];

    /* Axios errors carry the full response body */
    const axiosErr = createContact.error as {
      response?: {
        data?: { errors?: { key?: string; message?: string }[] };
      };
      message?: string;
    };
    const apiErrors = axiosErr?.response?.data?.errors;
    if (Array.isArray(apiErrors) && apiErrors.length > 0) {
      return apiErrors
        .map((item) => item.message ?? 'Unknown error')
        .filter(Boolean);
    }

    /* Fallback: generic Error from the response interceptor */
    if (axiosErr?.message) return [axiosErr.message];

    return ['An unexpected error occurred. Please try again.'];
  }, [createContact.isError, createContact.error]);

  /** Navigate back to the contacts list (cancel action). */
  const handleCancel = useCallback(() => {
    navigate('/crm/contacts');
  }, [navigate]);

  /* ================================================================ */
  /*  Render helpers                                                   */
  /* ================================================================ */

  /** Renders a labelled text / email / tel input with error state. */
  const renderTextField = (
    name: keyof ContactFormState,
    label: string,
    type: 'text' | 'email' | 'tel' = 'text',
    placeholder = '',
  ) => {
    const hasError = Boolean(errors[name]);
    return (
      <div>
        <label
          htmlFor={`contact-${name}`}
          className="block text-sm font-medium text-gray-700"
        >
          {label}
        </label>
        <input
          id={`contact-${name}`}
          name={name}
          type={type}
          value={formState[name]}
          onChange={handleInputChange}
          placeholder={placeholder}
          aria-invalid={hasError ? 'true' : undefined}
          aria-describedby={hasError ? `contact-${name}-error` : undefined}
          className={[
            'mt-1 block w-full rounded-md border-0 py-1.5 shadow-sm ring-1 ring-inset sm:text-sm sm:leading-6',
            hasError
              ? 'text-red-900 ring-red-300 placeholder:text-red-300 focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-red-500'
              : 'text-gray-900 ring-gray-300 placeholder:text-gray-400 focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-indigo-600',
          ].join(' ')}
        />
        {hasError && (
          <p
            id={`contact-${name}-error`}
            className="mt-1 text-sm text-red-600"
            role="alert"
          >
            {errors[name]}
          </p>
        )}
      </div>
    );
  };

  /* ================================================================ */
  /*  JSX                                                              */
  /* ================================================================ */

  return (
    <div className="mx-auto max-w-4xl px-4 py-8 sm:px-6 lg:px-8">
      {/* ---- Page header ---- */}
      <div className="mb-8">
        <h1 className="text-2xl font-bold tracking-tight text-gray-900">
          Create Contact
        </h1>
        <p className="mt-1 text-sm text-gray-500">
          Add a new contact to the CRM system.
        </p>
      </div>

      {/* ---- Server-side error banner ---- */}
      {serverErrors.length > 0 && (
        <div
          className="mb-6 rounded-lg border border-red-200 bg-red-50 p-4"
          role="alert"
        >
          <div className="flex items-start gap-3">
            <svg
              className="size-5 shrink-0 text-red-400"
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
            <div>
              <h3 className="text-sm font-medium text-red-800">
                There were errors creating the contact
              </h3>
              <ul className="mt-2 list-disc space-y-1 ps-5 text-sm text-red-700">
                {serverErrors.map((msg, idx) => (
                  <li key={idx}>{msg}</li>
                ))}
              </ul>
            </div>
          </div>
        </div>
      )}

      <form onSubmit={handleSubmit} noValidate className="space-y-8">
        {/* ========================================================== */}
        {/*  PHOTO & IDENTITY SECTION                                   */}
        {/* ========================================================== */}
        <section className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
          <h2 className="mb-6 text-lg font-semibold text-gray-900">
            Identity
          </h2>

          {/* Photo upload area */}
          <div className="mb-6">
            <span className="mb-2 block text-sm font-medium text-gray-700">
              Photo
            </span>
            <div
              onDragOver={handleDragOver}
              onDragLeave={handleDragLeave}
              onDrop={handleDrop}
              className={[
                'relative flex flex-col items-center justify-center rounded-lg border-2 border-dashed p-6 transition-colors',
                isDragActive
                  ? 'border-indigo-400 bg-indigo-50'
                  : errors.photo
                    ? 'border-red-300 bg-red-50'
                    : 'border-gray-300 bg-gray-50 hover:border-gray-400',
              ].join(' ')}
            >
              {photoPreview ? (
                <div className="flex flex-col items-center gap-4">
                  <img
                    src={photoPreview}
                    alt="Contact photo preview"
                    className="size-24 rounded-full object-cover ring-2 ring-gray-200"
                    width={96}
                    height={96}
                  />
                  {isUploading && (
                    <div className="w-48">
                      <div className="mb-1 flex justify-between text-xs text-gray-600">
                        <span>Uploading…</span>
                        <span>{progress.percentage}%</span>
                      </div>
                      <div className="h-1.5 w-full rounded-full bg-gray-200">
                        <div
                          className="h-1.5 rounded-full bg-indigo-600 transition-all"
                          style={{ width: `${progress.percentage}%` }}
                        />
                      </div>
                    </div>
                  )}
                  {isUploadError && (
                    <p className="text-sm text-red-600" role="alert">
                      {uploadError instanceof Error
                        ? uploadError.message
                        : 'Upload failed.'}
                    </p>
                  )}
                  <button
                    type="button"
                    onClick={handleRemovePhoto}
                    className="inline-flex items-center gap-1 rounded-md px-2.5 py-1.5 text-sm font-medium text-red-600 hover:text-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
                  >
                    <svg
                      className="size-4"
                      viewBox="0 0 20 20"
                      fill="currentColor"
                      aria-hidden="true"
                    >
                      <path
                        fillRule="evenodd"
                        d="M8.75 1A2.75 2.75 0 006 3.75v.443c-.795.077-1.584.176-2.365.298a.75.75 0 10.23 1.482l.149-.022.841 10.518A2.75 2.75 0 007.596 19h4.807a2.75 2.75 0 002.742-2.53l.841-10.52.149.023a.75.75 0 00.23-1.482A41.03 41.03 0 0014 4.193V3.75A2.75 2.75 0 0011.25 1h-2.5zM10 4c.84 0 1.673.025 2.5.075V3.75c0-.69-.56-1.25-1.25-1.25h-2.5c-.69 0-1.25.56-1.25 1.25v.325C8.327 4.025 9.16 4 10 4zM8.58 7.72a.75.75 0 00-1.5.06l.3 7.5a.75.75 0 101.5-.06l-.3-7.5zm4.34.06a.75.75 0 10-1.5-.06l-.3 7.5a.75.75 0 101.5.06l.3-7.5z"
                        clipRule="evenodd"
                      />
                    </svg>
                    Remove
                  </button>
                </div>
              ) : (
                <label
                  htmlFor="contact-photo-input"
                  className="flex cursor-pointer flex-col items-center gap-2"
                >
                  <svg
                    className="size-10 text-gray-400"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth={1.5}
                    aria-hidden="true"
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      d="M2.25 15.75l5.159-5.159a2.25 2.25 0 013.182 0l5.159 5.159m-1.5-1.5l1.409-1.409a2.25 2.25 0 013.182 0l2.909 2.909M3.75 21h16.5A2.25 2.25 0 0022.5 18.75V5.25A2.25 2.25 0 0020.25 3H3.75A2.25 2.25 0 001.5 5.25v13.5A2.25 2.25 0 003.75 21z"
                    />
                  </svg>
                  <span className="text-sm font-medium text-indigo-600 hover:text-indigo-500">
                    Upload a photo
                  </span>
                  <span className="text-xs text-gray-500">
                    or drag and drop — JPG, PNG, GIF, WebP
                  </span>
                  <input
                    id="contact-photo-input"
                    type="file"
                    accept="image/jpeg,image/png,image/gif,image/webp"
                    onChange={handlePhotoUpload}
                    className="sr-only"
                  />
                </label>
              )}
            </div>
            {errors.photo && !isUploadError && (
              <p className="mt-1 text-sm text-red-600" role="alert">
                {errors.photo}
              </p>
            )}
          </div>

          {/* Identity fields — responsive 2-column grid */}
          <div className="grid grid-cols-1 gap-x-6 gap-y-4 sm:grid-cols-2">
            {/* Salutation dropdown */}
            <div>
              <label
                htmlFor="contact-salutation_id"
                className="block text-sm font-medium text-gray-700"
              >
                Salutation
              </label>
              <select
                id="contact-salutation_id"
                name="salutation_id"
                value={formState.salutation_id}
                onChange={handleInputChange}
                disabled={isSalutationsLoading}
                className="mt-1 block w-full rounded-md border-0 py-1.5 text-gray-900 shadow-sm ring-1 ring-inset ring-gray-300 focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-indigo-600 sm:text-sm sm:leading-6"
              >
                <option value="">— None —</option>
                {salutationOptions.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
            </div>

            {renderTextField('job_title', 'Job Title', 'text', 'e.g. Senior Developer')}
            {renderTextField('first_name', 'First Name', 'text', 'Jane')}
            {renderTextField('last_name', 'Last Name', 'text', 'Doe')}
          </div>
        </section>

        {/* ========================================================== */}
        {/*  CONTACT INFORMATION SECTION                                */}
        {/* ========================================================== */}
        <section className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
          <h2 className="mb-6 text-lg font-semibold text-gray-900">
            Contact Information
          </h2>
          <div className="grid grid-cols-1 gap-x-6 gap-y-4 sm:grid-cols-2">
            {renderTextField('email', 'Email', 'email', 'jane.doe@example.com')}
            {renderTextField('fixed_phone', 'Fixed Phone', 'tel', '+1 (555) 123-4567')}
            {renderTextField('mobile_phone', 'Mobile Phone', 'tel', '+1 (555) 987-6543')}
            {renderTextField('fax_phone', 'Fax', 'tel', '+1 (555) 111-2222')}
          </div>
        </section>

        {/* ========================================================== */}
        {/*  ADDRESS SECTION                                            */}
        {/* ========================================================== */}
        <section className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
          <h2 className="mb-6 text-lg font-semibold text-gray-900">
            Address
          </h2>
          <div className="grid grid-cols-1 gap-x-6 gap-y-4 sm:grid-cols-2">
            <div className="sm:col-span-2">
              {renderTextField('street', 'Street', 'text', '123 Main St')}
            </div>
            <div className="sm:col-span-2">
              {renderTextField('street_2', 'Street Line 2', 'text', 'Suite 400')}
            </div>
            {renderTextField('city', 'City', 'text', 'San Francisco')}
            {renderTextField('region', 'State / Region', 'text', 'California')}
            {renderTextField('post_code', 'Postal Code', 'text', '94105')}

            {/* Country dropdown */}
            <div>
              <label
                htmlFor="contact-country_id"
                className="block text-sm font-medium text-gray-700"
              >
                Country
              </label>
              <select
                id="contact-country_id"
                name="country_id"
                value={formState.country_id}
                onChange={handleInputChange}
                disabled={isCountriesLoading}
                className="mt-1 block w-full rounded-md border-0 py-1.5 text-gray-900 shadow-sm ring-1 ring-inset ring-gray-300 focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-indigo-600 sm:text-sm sm:leading-6"
              >
                <option value="">— Select a country —</option>
                {countryOptions.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
            </div>
          </div>
        </section>

        {/* ========================================================== */}
        {/*  NOTES SECTION                                              */}
        {/* ========================================================== */}
        <section className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
          <h2 className="mb-6 text-lg font-semibold text-gray-900">Notes</h2>
          <div>
            <label htmlFor="contact-notes" className="sr-only">
              Notes
            </label>
            <textarea
              id="contact-notes"
              name="notes"
              rows={4}
              value={formState.notes}
              onChange={handleInputChange}
              placeholder="Add any additional notes about this contact…"
              className="block w-full rounded-md border-0 py-1.5 text-gray-900 shadow-sm ring-1 ring-inset ring-gray-300 placeholder:text-gray-400 focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-indigo-600 sm:text-sm sm:leading-6"
            />
          </div>
        </section>

        {/* ========================================================== */}
        {/*  ACTION BUTTONS                                             */}
        {/* ========================================================== */}
        <div className="flex items-center justify-end gap-4">
          <button
            type="button"
            onClick={handleCancel}
            className="rounded-md bg-white px-4 py-2 text-sm font-semibold text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-600"
          >
            Cancel
          </button>
          <button
            type="submit"
            disabled={createContact.isPending || isUploading}
            className="inline-flex items-center gap-2 rounded-md bg-indigo-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-indigo-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {createContact.isPending && (
              <svg
                className="size-4 animate-spin"
                viewBox="0 0 24 24"
                fill="none"
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
                  d="M4 12a8 8 0 018-8v4a4 4 0 00-4 4H4z"
                />
              </svg>
            )}
            {createContact.isPending ? 'Creating…' : 'Create Contact'}
          </button>
        </div>
      </form>
    </div>
  );
}