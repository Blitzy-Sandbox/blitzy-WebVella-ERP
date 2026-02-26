/**
 * ContactManage — Edit form for an existing CRM contact.
 *
 * Route: /crm/contacts/:id/manage
 *
 * Pre-populates all fields from the existing contact record,
 * tracks dirty state, provides photo management (view / replace / remove),
 * and supports delete-with-confirmation via a Modal dialog.
 *
 * Replaces the monolith's RecordManage.cshtml.cs for the "contact" entity.
 */

/* ================================================================ */
/*  External imports                                                 */
/* ================================================================ */
import { useState, useEffect, useCallback, useMemo } from 'react';
import type { FormEvent, ChangeEvent, DragEvent } from 'react';
import { useParams, useNavigate, useBlocker } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';

/* ================================================================ */
/*  Internal imports                                                 */
/* ================================================================ */
import apiClient from '../../api/client';
import { useDeleteContact } from '../../hooks/useCrm';
import type { EntityRecord } from '../../types/record';
import { useFileUpload } from '../../hooks/useFiles';
import Modal from '../../components/common/Modal';

/* ================================================================ */
/*  Local types                                                      */
/* ================================================================ */

/** Mirrors every editable contact field as a string for controlled inputs. */
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

interface ValidationErrors {
  [key: string]: string;
}

interface SelectOption {
  value: string;
  label: string;
}

/* ================================================================ */
/*  Constants                                                        */
/* ================================================================ */

/** Default salutation GUID seeded by NextPlugin.20190206. */
const DEFAULT_SALUTATION_ID = '87c08ee1-8d4d-4c89-9b37-4e3cc3f98698';

/** 30 minutes — keeps reference-data dropdown queries fresh but not chatty. */
const REFERENCE_DATA_STALE_TIME = 30 * 60 * 1000;

const EMAIL_REGEX = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
const PHONE_REGEX = /^[+]?[\d\s\-().]{3,30}$/;

const ALLOWED_IMAGE_TYPES = new Set([
  'image/jpeg',
  'image/png',
  'image/gif',
  'image/webp',
]);

/** Blank form used as a fallback before the record loads. */
const INITIAL_FORM_STATE: ContactFormState = {
  salutation_id: '',
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

/* ================================================================ */
/*  Helpers                                                          */
/* ================================================================ */

/**
 * Extracts a ContactFormState from the raw EntityRecord returned by the
 * CRM service.  Unknown keys are silently ignored; missing keys default
 * to the empty string.
 */
function recordToFormState(record: EntityRecord): ContactFormState {
  return {
    salutation_id: String(record.salutation_id ?? ''),
    first_name: String(record.first_name ?? ''),
    last_name: String(record.last_name ?? ''),
    job_title: String(record.job_title ?? ''),
    email: String(record.email ?? ''),
    fixed_phone: String(record.fixed_phone ?? ''),
    mobile_phone: String(record.mobile_phone ?? ''),
    fax_phone: String(record.fax_phone ?? ''),
    street: String(record.street ?? ''),
    street_2: String(record.street_2 ?? ''),
    city: String(record.city ?? ''),
    region: String(record.region ?? ''),
    post_code: String(record.post_code ?? ''),
    country_id: String(record.country_id ?? ''),
    photo: String(record.photo ?? ''),
    notes: String(record.notes ?? ''),
  };
}

/* ================================================================ */
/*  Component                                                        */
/* ================================================================ */

export default function ContactManage() {
  /* -------------------------------------------------------------- */
  /*  Routing                                                        */
  /* -------------------------------------------------------------- */
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  /* -------------------------------------------------------------- */
  /*  File upload hook (photo management)                            */
  /* -------------------------------------------------------------- */
  const {
    upload,
    progress,
    isUploading,
    isError: isUploadError,
    error: uploadError,
    reset: resetUpload,
  } = useFileUpload();

  /* -------------------------------------------------------------- */
  /*  Component-local state                                          */
  /* -------------------------------------------------------------- */
  const [formState, setFormState] = useState<ContactFormState>(INITIAL_FORM_STATE);
  const [originalState, setOriginalState] = useState<ContactFormState>(INITIAL_FORM_STATE);
  const [errors, setErrors] = useState<ValidationErrors>({});
  const [photoPreview, setPhotoPreview] = useState<string>('');
  const [isDragActive, setIsDragActive] = useState(false);
  const [showDeleteModal, setShowDeleteModal] = useState(false);

  /* -------------------------------------------------------------- */
  /*  Data-fetching queries                                          */
  /* -------------------------------------------------------------- */

  /** Fetch the existing contact record. */
  const {
    data: contactResponse,
    isLoading: isContactLoading,
    isError: isContactError,
  } = useQuery({
    queryKey: ['crm', 'contacts', id],
    queryFn: () => apiClient.get<EntityRecord>(`/v1/crm/contacts/${id}`),
    enabled: Boolean(id),
  });

  /** Salutation reference data for the dropdown. */
  const { data: salutationsResponse, isLoading: isSalutationsLoading } = useQuery({
    queryKey: ['crm', 'salutations'],
    queryFn: () => apiClient.get('/v1/crm/salutations'),
    staleTime: REFERENCE_DATA_STALE_TIME,
  });

  /** Country reference data for the dropdown. */
  const { data: countriesResponse, isLoading: isCountriesLoading } = useQuery({
    queryKey: ['crm', 'countries'],
    queryFn: () => apiClient.get('/v1/crm/countries'),
    staleTime: REFERENCE_DATA_STALE_TIME,
  });

  /* -------------------------------------------------------------- */
  /*  Mutations                                                      */
  /* -------------------------------------------------------------- */

  /** PUT /v1/crm/contacts/:id — update contact. */
  const updateContact = useMutation({
    mutationFn: (data: EntityRecord) =>
      apiClient.put<EntityRecord>(`/v1/crm/contacts/${id}`, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['crm', 'contacts', id] });
      queryClient.invalidateQueries({ queryKey: ['crm', 'contacts'] });
      navigate(`/crm/contacts/${id}`);
    },
  });

  /** DELETE /v1/crm/contacts/:id — delete contact via useCrm hook. */
  const deleteContact = useDeleteContact();

  /* -------------------------------------------------------------- */
  /*  Populate form when contact data arrives                        */
  /* -------------------------------------------------------------- */
  useEffect(() => {
    const record = contactResponse?.data?.object as EntityRecord | undefined;
    if (!record) return;

    const populated = recordToFormState(record);
    setFormState(populated);
    setOriginalState(populated);

    /* If the contact already has a photo URL, use it as the preview. */
    if (populated.photo) {
      setPhotoPreview(populated.photo);
    }
  }, [contactResponse]);

  /* -------------------------------------------------------------- */
  /*  Dirty tracking                                                 */
  /* -------------------------------------------------------------- */

  /**
   * Deep-compare every field in formState against the original values
   * loaded from the server to detect unsaved edits.
   */
  const isDirty = useMemo(() => {
    const keys = Object.keys(INITIAL_FORM_STATE) as (keyof ContactFormState)[];
    return keys.some((key) => formState[key] !== originalState[key]);
  }, [formState, originalState]);

  /**
   * Block in-app navigation when there are unsaved changes, UNLESS
   * the update mutation already succeeded (navigation is intentional).
   */
  const blocker = useBlocker(isDirty && !updateContact.isSuccess);

  /* -------------------------------------------------------------- */
  /*  Dropdown option memoisation                                    */
  /* -------------------------------------------------------------- */

  const salutationOptions: SelectOption[] = useMemo(() => {
    const raw = salutationsResponse?.data?.object;
    if (!raw) return [];
    const list = Array.isArray(raw) ? raw : (raw as { records?: EntityRecord[] })?.records;
    if (!Array.isArray(list)) return [];
    return list.map((s: EntityRecord) => ({
      value: String(s.id ?? ''),
      label: String(s.label ?? s.name ?? ''),
    }));
  }, [salutationsResponse]);

  const countryOptions: SelectOption[] = useMemo(() => {
    const raw = countriesResponse?.data?.object;
    if (!raw) return [];
    const list = Array.isArray(raw) ? raw : (raw as { records?: EntityRecord[] })?.records;
    if (!Array.isArray(list)) return [];
    return list.map((c: EntityRecord) => ({
      value: String(c.id ?? ''),
      label: String(c.name ?? c.label ?? ''),
    }));
  }, [countriesResponse]);

  /* -------------------------------------------------------------- */
  /*  Validation                                                     */
  /* -------------------------------------------------------------- */

  const validate = useCallback((): boolean => {
    const next: ValidationErrors = {};

    /* At least one of first_name / last_name is required. */
    if (!formState.first_name.trim() && !formState.last_name.trim()) {
      next.first_name = 'First name or last name is required.';
      next.last_name = 'First name or last name is required.';
    }

    /* Email format. */
    if (formState.email.trim() && !EMAIL_REGEX.test(formState.email.trim())) {
      next.email = 'Please enter a valid email address.';
    }

    /* Phone format (all three phone fields). */
    if (formState.fixed_phone.trim() && !PHONE_REGEX.test(formState.fixed_phone.trim())) {
      next.fixed_phone = 'Please enter a valid phone number.';
    }
    if (formState.mobile_phone.trim() && !PHONE_REGEX.test(formState.mobile_phone.trim())) {
      next.mobile_phone = 'Please enter a valid phone number.';
    }
    if (formState.fax_phone.trim() && !PHONE_REGEX.test(formState.fax_phone.trim())) {
      next.fax_phone = 'Please enter a valid phone number.';
    }

    setErrors(next);
    return Object.keys(next).length === 0;
  }, [formState]);

  /* -------------------------------------------------------------- */
  /*  Input handlers                                                 */
  /* -------------------------------------------------------------- */

  const handleInputChange = useCallback(
    (e: ChangeEvent<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>) => {
      const { name, value } = e.target;
      setFormState((prev) => ({ ...prev, [name]: value }));
      setErrors((prev) => {
        if (!prev[name]) return prev;
        const copy = { ...prev };
        delete copy[name];
        return copy;
      });
    },
    [],
  );

  /* -------------------------------------------------------------- */
  /*  Photo handlers                                                 */
  /* -------------------------------------------------------------- */

  const handlePhotoFile = useCallback(
    async (file: File) => {
      if (!ALLOWED_IMAGE_TYPES.has(file.type)) {
        setErrors((prev) => ({
          ...prev,
          photo: 'Please upload a JPG, PNG, GIF, or WebP image.',
        }));
        return;
      }

      setErrors((prev) => {
        if (!prev.photo) return prev;
        const copy = { ...prev };
        delete copy.photo;
        return copy;
      });

      /* Create a local blob preview immediately. */
      const previewUrl = URL.createObjectURL(file);
      setPhotoPreview(previewUrl);

      try {
        const result = await upload({ file });
        const uploadedUrl = (result as unknown as { object?: { url?: string } })?.object?.url;
        if (uploadedUrl) {
          setFormState((prev) => ({ ...prev, photo: uploadedUrl }));
        }
      } catch {
        /* Revert preview on failure. */
        URL.revokeObjectURL(previewUrl);
        setPhotoPreview(originalState.photo || '');
        setFormState((prev) => ({ ...prev, photo: originalState.photo }));
      }
    },
    [upload, originalState.photo],
  );

  const handlePhotoUpload = useCallback(
    (e: ChangeEvent<HTMLInputElement>) => {
      const file = e.target.files?.[0];
      if (file) handlePhotoFile(file);
    },
    [handlePhotoFile],
  );

  const handleDragOver = useCallback((e: DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    setIsDragActive(true);
  }, []);

  const handleDragLeave = useCallback((e: DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    setIsDragActive(false);
  }, []);

  const handleDrop = useCallback(
    (e: DragEvent<HTMLDivElement>) => {
      e.preventDefault();
      setIsDragActive(false);
      const file = e.dataTransfer?.files?.[0];
      if (file) handlePhotoFile(file);
    },
    [handlePhotoFile],
  );

  const handleRemovePhoto = useCallback(() => {
    if (photoPreview && photoPreview.startsWith('blob:')) {
      URL.revokeObjectURL(photoPreview);
    }
    setPhotoPreview('');
    setFormState((prev) => ({ ...prev, photo: '' }));
    resetUpload();
  }, [photoPreview, resetUpload]);

  /* -------------------------------------------------------------- */
  /*  Submit / Cancel / Delete handlers                              */
  /* -------------------------------------------------------------- */

  const handleSubmit = useCallback(
    (e: FormEvent) => {
      e.preventDefault();
      if (!validate()) return;

      /* Build the payload — only include non-empty, trimmed values. */
      const payload: EntityRecord = { id: id as string };
      (Object.keys(formState) as (keyof ContactFormState)[]).forEach((key) => {
        const trimmed = formState[key].trim();
        if (trimmed) {
          payload[key] = trimmed;
        }
      });

      updateContact.mutate(payload);
    },
    [formState, validate, id, updateContact],
  );

  const handleCancel = useCallback(() => {
    navigate(`/crm/contacts/${id}`);
  }, [navigate, id]);

  const handleDeleteConfirm = useCallback(async () => {
    if (!id) return;
    try {
      await deleteContact.mutateAsync(id);
      setShowDeleteModal(false);
      navigate('/crm/contacts');
    } catch {
      /* Error state is surfaced by the mutation's isError / error properties. */
    }
  }, [id, deleteContact, navigate]);

  /* -------------------------------------------------------------- */
  /*  Server error extraction                                        */
  /* -------------------------------------------------------------- */

  const serverErrors: string[] = useMemo(() => {
    if (!updateContact.isError) return [];
    const err = updateContact.error;
    const axiosErr = err as {
      response?: { data?: { errors?: { message?: string }[] } };
      message?: string;
    };
    const apiErrors = axiosErr?.response?.data?.errors;
    if (Array.isArray(apiErrors) && apiErrors.length > 0) {
      return apiErrors.map((item) => item.message ?? 'Unknown error').filter(Boolean);
    }
    if (axiosErr?.message) return [axiosErr.message];
    return ['An unexpected error occurred. Please try again.'];
  }, [updateContact.isError, updateContact.error]);

  /* ================================================================ */
  /*  Early-return states: loading / not found                        */
  /* ================================================================ */

  if (isContactLoading) {
    return (
      <div className="mx-auto max-w-4xl px-4 py-8 sm:px-6 lg:px-8">
        <div className="animate-pulse space-y-6">
          <div className="h-8 w-48 rounded bg-gray-200" />
          <div className="h-4 w-64 rounded bg-gray-200" />
          <div className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
            <div className="mb-6 h-6 w-32 rounded bg-gray-200" />
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              {Array.from({ length: 4 }).map((_, i) => (
                <div key={i} className="space-y-2">
                  <div className="h-4 w-24 rounded bg-gray-200" />
                  <div className="h-9 w-full rounded bg-gray-200" />
                </div>
              ))}
            </div>
          </div>
          <div className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
            <div className="mb-6 h-6 w-40 rounded bg-gray-200" />
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              {Array.from({ length: 4 }).map((_, i) => (
                <div key={i} className="space-y-2">
                  <div className="h-4 w-24 rounded bg-gray-200" />
                  <div className="h-9 w-full rounded bg-gray-200" />
                </div>
              ))}
            </div>
          </div>
        </div>
      </div>
    );
  }

  if (isContactError || !contactResponse?.data?.object) {
    return (
      <div className="mx-auto max-w-4xl px-4 py-8 sm:px-6 lg:px-8">
        <div className="rounded-lg border border-red-200 bg-red-50 p-8 text-center">
          <svg
            className="mx-auto size-12 text-red-400"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth={1.5}
            aria-hidden="true"
          >
            <path
              strokeLinecap="round"
              strokeLinejoin="round"
              d="M12 9v3.75m-9.303 3.376c-.866 1.5.217 3.374 1.948 3.374h14.71c1.73 0 2.813-1.874 1.948-3.374L13.949 3.378c-.866-1.5-3.032-1.5-3.898 0L2.697 16.126ZM12 15.75h.007v.008H12v-.008Z"
            />
          </svg>
          <h2 className="mt-4 text-lg font-semibold text-red-800">
            Contact Not Found
          </h2>
          <p className="mt-2 text-sm text-red-600">
            The contact you are trying to edit does not exist or could not be loaded.
          </p>
          <button
            type="button"
            onClick={() => navigate('/crm/contacts')}
            className="mt-6 inline-flex items-center gap-2 rounded-md bg-red-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-red-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
          >
            Back to Contacts
          </button>
        </div>
      </div>
    );
  }

  /* ================================================================ */
  /*  Render helper: labelled text / email / tel input                */
  /* ================================================================ */

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
      {/* ---- Unsaved-changes blocker dialog ---- */}
      {blocker.state === 'blocked' && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/40"
          role="dialog"
          aria-modal="true"
          aria-labelledby="blocker-dialog-title"
        >
          <div className="mx-4 w-full max-w-md rounded-lg bg-white p-6 shadow-xl">
            <h2
              id="blocker-dialog-title"
              className="text-lg font-semibold text-gray-900"
            >
              Unsaved Changes
            </h2>
            <p className="mt-2 text-sm text-gray-600">
              You have unsaved changes. Are you sure you want to leave this
              page? Your changes will be lost.
            </p>
            <div className="mt-6 flex justify-end gap-3">
              <button
                type="button"
                onClick={() => blocker.reset?.()}
                className="rounded-md bg-white px-4 py-2 text-sm font-semibold text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-600"
              >
                Stay on Page
              </button>
              <button
                type="button"
                onClick={() => blocker.proceed?.()}
                className="rounded-md bg-red-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-red-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
              >
                Leave Page
              </button>
            </div>
          </div>
        </div>
      )}

      {/* ---- Page header ---- */}
      <div className="mb-8">
        <h1 className="text-2xl font-bold tracking-tight text-gray-900">
          Edit Contact
        </h1>
        <p className="mt-1 text-sm text-gray-500">
          Update the details for this contact.
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
                There were errors updating the contact
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

          {/* Photo upload / display area */}
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
                  <div className="flex items-center gap-3">
                    {/* Change Photo button */}
                    <label
                      htmlFor="contact-photo-input"
                      className="inline-flex cursor-pointer items-center gap-1 rounded-md px-2.5 py-1.5 text-sm font-medium text-indigo-600 hover:text-indigo-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                    >
                      <svg
                        className="size-4"
                        viewBox="0 0 20 20"
                        fill="currentColor"
                        aria-hidden="true"
                      >
                        <path d="M2.695 14.763l-1.262 3.154a.5.5 0 00.65.65l3.155-1.262a4 4 0 001.343-.885L17.5 5.5a2.121 2.121 0 00-3-3L3.58 13.42a4 4 0 00-.885 1.343z" />
                      </svg>
                      Change Photo
                      <input
                        id="contact-photo-input"
                        type="file"
                        accept="image/jpeg,image/png,image/gif,image/webp"
                        onChange={handlePhotoUpload}
                        className="sr-only"
                      />
                    </label>

                    {/* Remove Photo button */}
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
                </div>
              ) : (
                <label
                  htmlFor="contact-photo-input-empty"
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
                    id="contact-photo-input-empty"
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
        <div className="flex items-center justify-between">
          {/* Delete — left-aligned danger action */}
          <button
            type="button"
            onClick={() => setShowDeleteModal(true)}
            disabled={deleteContact.isPending}
            className="inline-flex items-center gap-2 rounded-md bg-red-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-red-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {deleteContact.isPending && (
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
            Delete
          </button>

          {/* Cancel + Save — right-aligned */}
          <div className="flex items-center gap-4">
            <button
              type="button"
              onClick={handleCancel}
              className="rounded-md bg-white px-4 py-2 text-sm font-semibold text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-600"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={updateContact.isPending || isUploading || !isDirty}
              className="inline-flex items-center gap-2 rounded-md bg-indigo-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-indigo-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {updateContact.isPending && (
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
              {updateContact.isPending ? 'Saving…' : 'Save Changes'}
            </button>
          </div>
        </div>
      </form>

      {/* ========================================================== */}
      {/*  DELETE CONFIRMATION MODAL                                   */}
      {/* ========================================================== */}
      <Modal
        isVisible={showDeleteModal}
        title="Confirm Delete"
        backdrop="static"
        onClose={() => setShowDeleteModal(false)}
        footer={
          <div className="flex justify-end gap-3">
            <button
              type="button"
              onClick={() => setShowDeleteModal(false)}
              className="rounded-md bg-white px-4 py-2 text-sm font-semibold text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-600"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={handleDeleteConfirm}
              disabled={deleteContact.isPending}
              className="inline-flex items-center gap-2 rounded-md bg-red-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-red-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {deleteContact.isPending && (
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
              Delete Contact
            </button>
          </div>
        }
      >
        <p className="text-sm text-gray-600">
          Are you sure you want to permanently delete this contact? This action
          cannot be undone.
        </p>
      </Modal>
    </div>
  );
}
