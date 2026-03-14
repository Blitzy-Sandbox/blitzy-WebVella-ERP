/**
 * Vitest unit tests for 13 entity/field/relation CRUD TanStack Query hooks.
 *
 * Hooks under test (from useEntities.ts):
 *   useEntities, useEntity, useCreateEntity, useUpdateEntity, useDeleteEntity,
 *   useCreateField, useUpdateField, useDeleteField,
 *   useRelations, useRelation, useCreateRelation, useUpdateRelation, useDeleteRelation
 *
 * Replaces monolith subsystems:
 *   - EntityManager.cs   — entity/field lifecycle with validation, security gating, cache management
 *   - EntityRelationManager.cs — relation CRUD with immutability rules
 *   - Cache.cs            — IMemoryCache wrapper for entities/relations (5-min staleTime in SPA)
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import React, { type ReactNode } from 'react';

// ── Module under test ────────────────────────────────────────────────
import {
  useEntities,
  useEntity,
  useCreateEntity,
  useUpdateEntity,
  useDeleteEntity,
  useCreateField,
  useUpdateField,
  useDeleteField,
  useRelations,
  useRelation,
  useCreateRelation,
  useUpdateRelation,
  useDeleteRelation,
} from '../../../src/hooks/useEntities';

// ── Type imports (type-only) ─────────────────────────────────────────
import type {
  Entity,
  InputEntity,
  EntityRelation,
  Field,
  AnyField,
  EntityListResponse,
  EntityResponse,
  EntityRelationResponse,
  EntityRelationListResponse,
} from '../../../src/types/entity';

import type { BaseResponseModel } from '../../../src/types/common';

// ── Mock API client ──────────────────────────────────────────────────
// vi.mock is auto-hoisted before imports so the mock is established
// before the hook module loads and binds to the real client functions.
vi.mock('../../../src/api/client', () => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  del: vi.fn(),
}));

import { get, post, put, del } from '../../../src/api/client';

const mockedGet = vi.mocked(get);
const mockedPost = vi.mocked(post);
const mockedPut = vi.mocked(put);
const mockedDel = vi.mocked(del);

// =====================================================================
// Test Fixtures
// =====================================================================

/** TextField = 18 (FieldType const enum numeric value) */
const FIELD_TYPE_TEXT = 18;
/** EmailField = 6 */
const FIELD_TYPE_EMAIL = 6;
/** SelectField = 17 */
const FIELD_TYPE_SELECT = 17;
/** PhoneField = 15 */
const FIELD_TYPE_PHONE = 15;
/** GuidField = 16 */
const FIELD_TYPE_GUID = 16;
/** NumberField = 12 */
const FIELD_TYPE_NUMBER = 12;
/** DateField = 4 */
const FIELD_TYPE_DATE = 4;
/** OneToMany = 2 (EntityRelationType const enum) */
const RELATION_TYPE_ONE_TO_MANY = 2;

const mockField: Field = {
  id: 'f1000000-0000-0000-0000-000000000001',
  name: 'name',
  label: 'Name',
  placeholderText: 'Enter name',
  description: 'Account name field',
  helpText: '',
  required: true,
  unique: true,
  searchable: true,
  auditable: false,
  system: false,
  permissions: { canRead: [], canUpdate: [] },
  enableSecurity: false,
  entityName: 'account',
  fieldType: FIELD_TYPE_TEXT as any,
};

const mockEntity: Entity = {
  id: 'e1000000-0000-0000-0000-000000000001',
  name: 'account',
  label: 'Account',
  labelPlural: 'Accounts',
  system: false,
  iconName: 'fa fa-building',
  color: '#2196f3',
  recordPermissions: {
    canRead: ['a1000000-0000-0000-0000-000000000001'],
    canCreate: ['a1000000-0000-0000-0000-000000000001'],
    canUpdate: ['a1000000-0000-0000-0000-000000000001'],
    canDelete: ['a1000000-0000-0000-0000-000000000001'],
  },
  fields: [mockField],
  recordScreenIdField: null,
  hash: 'abc123hash',
};

const mockSecondEntity: Entity = {
  ...mockEntity,
  id: 'e1000000-0000-0000-0000-000000000002',
  name: 'contact',
  label: 'Contact',
  labelPlural: 'Contacts',
  iconName: 'fa fa-user',
  color: '#4caf50',
  fields: [],
  hash: 'def456hash',
};

const mockInputEntity: InputEntity = {
  name: 'contact',
  label: 'Contact',
  labelPlural: 'Contacts',
  iconName: 'fa fa-user',
  color: '#4caf50',
  recordPermissions: {
    canRead: [],
    canCreate: [],
    canUpdate: [],
    canDelete: [],
  },
};

const mockRelation: EntityRelation = {
  id: 'r1000000-0000-0000-0000-000000000001',
  name: 'account_contact',
  label: 'Account → Contact',
  description: 'Links accounts to contacts',
  system: false,
  relationType: RELATION_TYPE_ONE_TO_MANY as any,
  originEntityId: 'e1000000-0000-0000-0000-000000000001',
  originFieldId: 'f1000000-0000-0000-0000-000000000001',
  targetEntityId: 'e1000000-0000-0000-0000-000000000002',
  targetFieldId: 'f1000000-0000-0000-0000-000000000002',
  originEntityName: 'account',
  originFieldName: 'id',
  targetEntityName: 'contact',
  targetFieldName: 'account_id',
};

// =====================================================================
// Response Helpers
// =====================================================================

/**
 * Build a successful API response envelope matching the `ApiResponse<T>` shape.
 * Covers BaseResponseModel fields: success, errors, message, timestamp, hash.
 */
function createSuccessResponse<T>(object: T): {
  success: BaseResponseModel['success'];
  errors: BaseResponseModel['errors'];
  message: BaseResponseModel['message'];
  timestamp: BaseResponseModel['timestamp'];
  hash: string | undefined;
  object: T;
  statusCode: number;
} {
  return {
    success: true,
    object,
    errors: [],
    statusCode: 200,
    timestamp: new Date().toISOString(),
    message: '',
    hash: 'response-hash',
  };
}

/**
 * Build an error response envelope (400/403/500).
 * The `assertApiSuccess` helper inside each hook checks `response.success`
 * and throws with the concatenated `errors[].message` values.
 */
function createErrorResponse(
  statusCode: number,
  errors: Array<{ key: string; value: string; message: string }>,
  message?: string,
) {
  return {
    success: false as const,
    object: undefined,
    errors,
    statusCode,
    timestamp: new Date().toISOString(),
    message: message || errors[0]?.message || 'Error',
    hash: undefined,
  };
}

// =====================================================================
// QueryClient Wrapper
// =====================================================================

let queryClient: QueryClient;

/**
 * Creates a React wrapper that provides QueryClientProvider context.
 * Uses React.createElement (not JSX) because the file is .ts not .tsx.
 */
function createWrapper() {
  return function Wrapper({ children }: { children: ReactNode }) {
    return React.createElement(QueryClientProvider, { client: queryClient }, children);
  };
}

// =====================================================================
// Setup / Teardown
// =====================================================================

beforeEach(() => {
  queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
  vi.clearAllMocks();
});

afterEach(() => {
  queryClient.clear();
});

// #####################################################################
//  1. useEntities — List all entities
//     Replaces EntityManager.ReadEntities() + Cache.cs 5-min TTL
// #####################################################################

describe('useEntities', () => {
  it('should fetch all entities', async () => {
    const entities: Entity[] = [mockEntity, mockSecondEntity];
    mockedGet.mockResolvedValueOnce(createSuccessResponse(entities));

    const { result } = renderHook(() => useEntities(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith('/entities');
    expect(result.current.data).toHaveLength(2);
    expect(result.current.data![0].name).toBe('account');
    expect(result.current.data![1].name).toBe('contact');
  });

  it('should use staleTime of 5 minutes', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse([mockEntity]));

    const { result } = renderHook(() => useEntities(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // A second render within the same QueryClient should re-use cached data
    // without issuing another network request (staleTime = 300 000 ms).
    renderHook(() => useEntities(), { wrapper: createWrapper() });
    expect(mockedGet).toHaveBeenCalledTimes(1);
  });

  it('should handle empty entity list', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse([] as Entity[]));

    const { result } = renderHook(() => useEntities(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual([]);
  });

  it('should propagate server error (500)', async () => {
    mockedGet.mockRejectedValueOnce(new Error('Internal server error'));

    const { result } = renderHook(() => useEntities(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toBe('Internal server error');
  });
});

// #####################################################################
//  2. useEntity — Fetch single entity by ID or name
//     Replaces EntityManager.ReadEntity(id / name)
// #####################################################################

describe('useEntity', () => {
  it('should fetch entity by ID', async () => {
    const entityId = 'e1000000-0000-0000-0000-000000000001';
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockEntity));

    const { result } = renderHook(() => useEntity(entityId), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith(`/entities/${entityId}`);
    expect(result.current.data).toEqual(mockEntity);
  });

  it('should fetch entity by name', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockEntity));

    const { result } = renderHook(() => useEntity('account'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith('/entities/account');
    expect(result.current.data?.name).toBe('account');
  });

  it('should include all fields with type-specific properties', async () => {
    const entityWithMultipleFields: Entity = {
      ...mockEntity,
      fields: [
        { ...mockField, name: 'name', fieldType: FIELD_TYPE_TEXT as any },
        { ...mockField, id: 'f2', name: 'email', fieldType: FIELD_TYPE_EMAIL as any },
        { ...mockField, id: 'f3', name: 'phone', fieldType: FIELD_TYPE_PHONE as any },
        { ...mockField, id: 'f4', name: 'status', fieldType: FIELD_TYPE_SELECT as any },
        { ...mockField, id: 'f5', name: 'guid', fieldType: FIELD_TYPE_GUID as any },
        { ...mockField, id: 'f6', name: 'amount', fieldType: FIELD_TYPE_NUMBER as any },
        { ...mockField, id: 'f7', name: 'created', fieldType: FIELD_TYPE_DATE as any },
      ],
    };
    mockedGet.mockResolvedValueOnce(createSuccessResponse(entityWithMultipleFields));

    const { result } = renderHook(() => useEntity('account'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data?.fields).toHaveLength(7);
    expect(result.current.data?.fields[0].fieldType).toBe(FIELD_TYPE_TEXT);
    expect(result.current.data?.fields[1].fieldType).toBe(FIELD_TYPE_EMAIL);
    expect(result.current.data?.fields[3].fieldType).toBe(FIELD_TYPE_SELECT);
  });

  it('should not fetch when idOrName is empty string', async () => {
    const { result } = renderHook(() => useEntity(''), {
      wrapper: createWrapper(),
    });

    // enabled = !!idOrName evaluates to false for empty string
    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedGet).not.toHaveBeenCalled();
  });

  it('should use staleTime of 5 minutes', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockEntity));

    const { result } = renderHook(() => useEntity('account'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Second render within same QueryClient reuses cached data
    renderHook(() => useEntity('account'), { wrapper: createWrapper() });
    expect(mockedGet).toHaveBeenCalledTimes(1);
  });
});

// #####################################################################
//  3. useCreateEntity — Create a new entity
//     Replaces EntityManager.CreateEntity(entity) with validation
// #####################################################################

describe('useCreateEntity', () => {
  it('should create entity successfully', async () => {
    const createdEntity: Entity = { ...mockSecondEntity };
    mockedPost.mockResolvedValueOnce(createSuccessResponse(createdEntity));

    const { result } = renderHook(() => useCreateEntity(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockInputEntity);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith('/entities', mockInputEntity);
    expect(result.current.data).toEqual(createdEntity);
  });

  it('should invalidate entities query on success', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockSecondEntity));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateEntity(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockInputEntity);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['entities'] }),
    );
  });

  it('should handle validation errors — name uniqueness and 63-char limit (400)', async () => {
    mockedPost.mockResolvedValueOnce(
      createErrorResponse(400, [
        { key: 'name', value: '', message: 'Entity name must be unique' },
        { key: 'name', value: '', message: 'Entity name must be less than 63 characters' },
      ]),
    );

    const { result } = renderHook(() => useCreateEntity(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockInputEntity);
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toContain('Entity name must be unique');
  });

  it('should handle permission denied — meta permission enforcement (403)', async () => {
    mockedPost.mockResolvedValueOnce(
      createErrorResponse(403, [
        { key: '', value: '', message: 'You do not have meta permissions to create entities' },
      ]),
    );

    const { result } = renderHook(() => useCreateEntity(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockInputEntity);
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toContain('meta permissions');
  });

  it('should handle server error (500)', async () => {
    mockedPost.mockRejectedValueOnce(new Error('Internal server error'));

    const { result } = renderHook(() => useCreateEntity(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockInputEntity);
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toBe('Internal server error');
  });
});

// #####################################################################
//  4. useUpdateEntity — Update an existing entity
//     Replaces EntityManager.UpdateEntity(entity)
// #####################################################################

describe('useUpdateEntity', () => {
  const entityId = 'e1000000-0000-0000-0000-000000000001';

  it('should update entity', async () => {
    const updatedEntity: Entity = { ...mockEntity, label: 'Updated Account' };
    mockedPut.mockResolvedValueOnce(createSuccessResponse(updatedEntity));

    const { result } = renderHook(() => useUpdateEntity(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ id: entityId, entity: { ...mockInputEntity, name: 'account' } });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPut).toHaveBeenCalledWith(
      `/entities/${entityId}`,
      expect.objectContaining({ name: 'account' }),
    );
    expect(result.current.data).toEqual(updatedEntity);
  });

  it('should invalidate both entities list and specific entity', async () => {
    mockedPut.mockResolvedValueOnce(createSuccessResponse(mockEntity));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateEntity(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ id: entityId, entity: mockInputEntity });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Must invalidate both the entities list and the specific entity detail cache
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['entities'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['entities', entityId] }),
    );
  });
});

// #####################################################################
//  5. useDeleteEntity — Delete an entity (with cascade to records)
//     Replaces EntityManager.DeleteEntity(id)
// #####################################################################

describe('useDeleteEntity', () => {
  const entityId = 'e1000000-0000-0000-0000-000000000001';

  it('should delete entity', async () => {
    mockedDel.mockResolvedValueOnce(createSuccessResponse(undefined));

    const { result } = renderHook(() => useDeleteEntity(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(entityId);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockedDel).toHaveBeenCalledWith(`/entities/${entityId}`);
  });

  it('should invalidate entities and records queries (cascade)', async () => {
    mockedDel.mockResolvedValueOnce(createSuccessResponse(undefined));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteEntity(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(entityId);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Deleting an entity cascades — all associated records are deleted,
    // so both entity metadata and record caches must be invalidated.
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['entities'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['records'] }),
    );
  });
});

// #####################################################################
//  6. useCreateField — Create a field on an entity
//     Replaces EntityManager.CreateField(entityId, field)
// #####################################################################

describe('useCreateField', () => {
  const entityId = 'e1000000-0000-0000-0000-000000000001';

  it('should create field on entity', async () => {
    const newField = {
      ...mockField,
      id: 'f1000000-0000-0000-0000-000000000099',
      name: 'email',
      label: 'Email',
      fieldType: FIELD_TYPE_EMAIL as any,
    } as AnyField;

    const updatedEntity: Entity = {
      ...mockEntity,
      fields: [...mockEntity.fields, newField as Field],
    };
    mockedPost.mockResolvedValueOnce(createSuccessResponse(updatedEntity));

    const { result } = renderHook(() => useCreateField(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityId, field: newField });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith(
      `/entities/${entityId}/fields`,
      newField,
    );
  });

  it('should invalidate parent entity (field→entity cross-invalidation)', async () => {
    const newField = {
      ...mockField,
      name: 'phone',
      fieldType: FIELD_TYPE_PHONE as any,
    } as AnyField;
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockEntity));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateField(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityId, field: newField });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Field mutations must invalidate the entity list AND the specific entity
    // because fields are embedded inside the entity object.
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['entities'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['entities', entityId] }),
    );
  });

  it('should handle type-specific validation errors (400)', async () => {
    mockedPost.mockResolvedValueOnce(
      createErrorResponse(400, [
        { key: 'options', value: '', message: 'Select field must have at least one option' },
      ]),
    );

    const selectField = {
      ...mockField,
      name: 'status',
      fieldType: FIELD_TYPE_SELECT as any,
    } as AnyField;

    const { result } = renderHook(() => useCreateField(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityId, field: selectField });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toContain('Select field must have at least one option');
  });

  it('should handle multiple field types in payloads', async () => {
    // Verify the hook correctly forwards different field-type payloads
    const numberField = {
      ...mockField,
      id: 'f-num',
      name: 'amount',
      fieldType: FIELD_TYPE_NUMBER as any,
    } as AnyField;
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockEntity));

    const { result } = renderHook(() => useCreateField(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityId, field: numberField });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith(
      `/entities/${entityId}/fields`,
      expect.objectContaining({ name: 'amount', fieldType: FIELD_TYPE_NUMBER }),
    );
  });
});

// #####################################################################
//  7. useUpdateField — Update an existing field
//     Replaces EntityManager.UpdateField(entityId, field)
// #####################################################################

describe('useUpdateField', () => {
  const entityId = 'e1000000-0000-0000-0000-000000000001';
  const fieldId = 'f1000000-0000-0000-0000-000000000001';

  it('should update field', async () => {
    const updatedField: Field = { ...mockField, label: 'Full Name' };
    mockedPut.mockResolvedValueOnce(createSuccessResponse(mockEntity));

    const { result } = renderHook(() => useUpdateField(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityId, fieldId, field: updatedField });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPut).toHaveBeenCalledWith(
      `/entities/${entityId}/fields/${fieldId}`,
      updatedField,
    );
  });

  it('should invalidate parent entity on field update', async () => {
    mockedPut.mockResolvedValueOnce(createSuccessResponse(mockEntity));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateField(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityId, fieldId, field: mockField });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['entities'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['entities', entityId] }),
    );
  });
});

// #####################################################################
//  8. useDeleteField — Delete a field from an entity
//     Replaces EntityManager.DeleteField(entityId, fieldId)
// #####################################################################

describe('useDeleteField', () => {
  const entityId = 'e1000000-0000-0000-0000-000000000001';
  const fieldId = 'f1000000-0000-0000-0000-000000000001';

  it('should delete field', async () => {
    mockedDel.mockResolvedValueOnce(createSuccessResponse(undefined));

    const { result } = renderHook(() => useDeleteField(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityId, fieldId });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedDel).toHaveBeenCalledWith(
      `/entities/${entityId}/fields/${fieldId}`,
    );
  });

  it('should invalidate parent entity on field delete', async () => {
    mockedDel.mockResolvedValueOnce(createSuccessResponse(undefined));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteField(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityId, fieldId });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['entities'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['entities', entityId] }),
    );
  });
});

// #####################################################################
//  9. useRelations — List all entity relations
//     Replaces EntityRelationManager.Read() + Cache.cs TTL
// #####################################################################

describe('useRelations', () => {
  it('should fetch all relations', async () => {
    const relations: EntityRelation[] = [mockRelation];
    mockedGet.mockResolvedValueOnce(createSuccessResponse(relations));

    const { result } = renderHook(() => useRelations(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith('/relations');
    expect(result.current.data).toEqual(relations);
    expect(result.current.data![0].name).toBe('account_contact');
  });

  it('should use staleTime of 5 minutes', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse([mockRelation]));

    const { result } = renderHook(() => useRelations(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Second render reuses cached data within the 5-minute stale window
    renderHook(() => useRelations(), { wrapper: createWrapper() });
    expect(mockedGet).toHaveBeenCalledTimes(1);
  });

  it('should handle empty relation list', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse([] as EntityRelation[]));

    const { result } = renderHook(() => useRelations(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual([]);
  });
});

// #####################################################################
// 10. useRelation — Fetch single relation by ID or name
//     Replaces EntityRelationManager.Read(id / name)
// #####################################################################

describe('useRelation', () => {
  it('should fetch relation by ID', async () => {
    const relationId = 'r1000000-0000-0000-0000-000000000001';
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockRelation));

    const { result } = renderHook(() => useRelation(relationId), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith(`/relations/${relationId}`);
    expect(result.current.data).toEqual(mockRelation);
  });

  it('should fetch relation by name', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockRelation));

    const { result } = renderHook(() => useRelation('account_contact'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockedGet).toHaveBeenCalledWith('/relations/account_contact');
  });

  it('should not fetch when idOrName is empty string', async () => {
    const { result } = renderHook(() => useRelation(''), {
      wrapper: createWrapper(),
    });

    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedGet).not.toHaveBeenCalled();
  });

  it('should use staleTime of 5 minutes', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockRelation));

    const { result } = renderHook(
      () => useRelation('account_contact'),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    renderHook(() => useRelation('account_contact'), { wrapper: createWrapper() });
    expect(mockedGet).toHaveBeenCalledTimes(1);
  });
});

// #####################################################################
// 11. useCreateRelation — Create a new entity relation
//     Replaces EntityRelationManager.Create(relation) with entity/field validation
// #####################################################################

describe('useCreateRelation', () => {
  it('should create relation', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockRelation));

    const { result } = renderHook(() => useCreateRelation(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockRelation);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith('/relations', mockRelation);
    expect(result.current.data).toEqual(mockRelation);
  });

  it('should invalidate both relations and entities (relation→entities cross-invalidation)', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockRelation));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateRelation(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockRelation);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Relations connect two entities, so entity cache must also be refreshed
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['relations'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['entities'] }),
    );
  });

  it('should handle validation error for invalid entity references (400)', async () => {
    mockedPost.mockResolvedValueOnce(
      createErrorResponse(400, [
        { key: 'originEntityId', value: '', message: 'Origin entity does not exist' },
      ]),
    );

    const { result } = renderHook(() => useCreateRelation(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockRelation);
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toContain('Origin entity does not exist');
  });
});

// #####################################################################
// 12. useUpdateRelation — Update relation metadata (label/description only)
//     Replaces EntityRelationManager.Update(relation) — immutable structure
// #####################################################################

describe('useUpdateRelation', () => {
  const relationId = 'r1000000-0000-0000-0000-000000000001';

  it('should update relation metadata', async () => {
    const updated: EntityRelation = {
      ...mockRelation,
      label: 'Account → Contact (Updated)',
      description: 'Updated description',
    };
    mockedPut.mockResolvedValueOnce(createSuccessResponse(updated));

    const { result } = renderHook(() => useUpdateRelation(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        id: relationId,
        relation: { label: 'Account → Contact (Updated)', description: 'Updated description' },
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPut).toHaveBeenCalledWith(
      `/relations/${relationId}`,
      expect.objectContaining({ label: 'Account → Contact (Updated)' }),
    );
  });

  it('should invalidate relations list and specific relation', async () => {
    mockedPut.mockResolvedValueOnce(createSuccessResponse(mockRelation));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateRelation(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        id: relationId,
        relation: { description: 'Updated' },
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['relations'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['relations', relationId] }),
    );
  });

  it('should handle immutability violations (400)', async () => {
    // The monolith enforces that relationType, originEntityId, etc. cannot be
    // changed after creation. The server returns 400 on such attempts.
    mockedPut.mockResolvedValueOnce(
      createErrorResponse(400, [
        {
          key: 'relationType',
          value: '',
          message: 'Relation structural fields cannot be changed after creation',
        },
      ]),
    );

    const { result } = renderHook(() => useUpdateRelation(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        id: relationId,
        relation: { relationType: 3 as any }, // ManyToMany — attempting structural change
      });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toContain('cannot be changed after creation');
  });
});

// #####################################################################
// 13. useDeleteRelation — Delete an entity relation
//     Replaces EntityRelationManager.Delete(id)
// #####################################################################

describe('useDeleteRelation', () => {
  const relationId = 'r1000000-0000-0000-0000-000000000001';

  it('should delete relation', async () => {
    mockedDel.mockResolvedValueOnce(createSuccessResponse(undefined));

    const { result } = renderHook(() => useDeleteRelation(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(relationId);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockedDel).toHaveBeenCalledWith(`/relations/${relationId}`);
  });

  it('should invalidate relations and entities (relation→entities cross-invalidation)', async () => {
    mockedDel.mockResolvedValueOnce(createSuccessResponse(undefined));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteRelation(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(relationId);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['relations'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['entities'] }),
    );
  });
});

// #####################################################################
// Cross-Cutting Error Handling & Edge Cases
// #####################################################################

describe('Error handling across hooks', () => {
  it('should propagate network errors on entity fetch', async () => {
    mockedGet.mockRejectedValueOnce(new Error('Network error'));

    const { result } = renderHook(() => useEntities(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toBe('Network error');
  });

  it('should handle assertApiSuccess failure when object is missing', async () => {
    // When the API returns success=true but object is null/undefined,
    // the hook throws "No entity returned from server" (or similar).
    mockedGet.mockResolvedValueOnce({
      success: true,
      object: null,
      errors: [],
      statusCode: 200,
      timestamp: new Date().toISOString(),
      message: '',
    });

    const { result } = renderHook(() => useEntity('account'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
  });

  it('should propagate concatenated error messages from 400 response', async () => {
    mockedPost.mockResolvedValueOnce(
      createErrorResponse(400, [
        { key: 'name', value: '', message: 'Entity name is required' },
        { key: 'name', value: '', message: 'Entity name must start with a letter' },
      ]),
    );

    const { result } = renderHook(() => useCreateEntity(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ ...mockInputEntity, name: '' });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    // assertApiSuccess concatenates multiple error messages
    expect(result.current.error?.message).toContain('Entity name is required');
  });

  it('should handle server error (500) on relation creation', async () => {
    mockedPost.mockRejectedValueOnce(new Error('Internal server error'));

    const { result } = renderHook(() => useCreateRelation(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockRelation);
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toBe('Internal server error');
  });

  it('should handle API rejection on field deletion', async () => {
    mockedDel.mockRejectedValueOnce(new Error('Connection refused'));

    const { result } = renderHook(() => useDeleteField(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        entityId: 'e1000000-0000-0000-0000-000000000001',
        fieldId: 'f1000000-0000-0000-0000-000000000001',
      });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toBe('Connection refused');
  });

  it('should show loading state during entity creation', async () => {
    // Use a deferred promise to keep the mutation in pending state
    let resolvePromise: (value: any) => void;
    const pendingPromise = new Promise((resolve) => {
      resolvePromise = resolve;
    });
    mockedPost.mockReturnValueOnce(pendingPromise as any);

    const { result } = renderHook(() => useCreateEntity(), {
      wrapper: createWrapper(),
    });

    act(() => {
      result.current.mutate(mockInputEntity);
    });

    // Wait for React to flush the state update — isPending should be true while the promise is unresolved
    await waitFor(() => {
      expect(result.current.isPending).toBe(true);
    });

    // Resolve to clean up and verify successful completion
    await act(async () => {
      resolvePromise!(createSuccessResponse(mockSecondEntity));
    });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
  });

  it('should handle permission denied (403) on field update', async () => {
    mockedPut.mockResolvedValueOnce(
      createErrorResponse(403, [
        { key: '', value: '', message: 'You do not have meta permissions to update fields' },
      ]),
    );

    const { result } = renderHook(() => useUpdateField(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        entityId: 'e1000000-0000-0000-0000-000000000001',
        fieldId: 'f1000000-0000-0000-0000-000000000001',
        field: mockField,
      });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toContain('meta permissions');
  });
});

// #####################################################################
// Cache Invalidation Summary Verification
// #####################################################################

describe('Cache invalidation patterns', () => {
  it('useCreateEntity → invalidates [entities]', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockSecondEntity));
    const spy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateEntity(), { wrapper: createWrapper() });
    await act(async () => { result.current.mutate(mockInputEntity); });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const calls = spy.mock.calls.map((c) => c[0]);
    expect(calls).toContainEqual(expect.objectContaining({ queryKey: ['entities'] }));
  });

  it('useUpdateEntity → invalidates [entities] AND [entities, id]', async () => {
    const id = 'e1000000-0000-0000-0000-000000000001';
    mockedPut.mockResolvedValueOnce(createSuccessResponse(mockEntity));
    const spy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateEntity(), { wrapper: createWrapper() });
    await act(async () => { result.current.mutate({ id, entity: mockInputEntity }); });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const keys = spy.mock.calls.map((c) => (c[0] as any)?.queryKey);
    expect(keys).toContainEqual(['entities']);
    expect(keys).toContainEqual(['entities', id]);
  });

  it('useDeleteEntity → invalidates [entities] AND [records]', async () => {
    mockedDel.mockResolvedValueOnce(createSuccessResponse(undefined));
    const spy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteEntity(), { wrapper: createWrapper() });
    await act(async () => { result.current.mutate('e1'); });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const keys = spy.mock.calls.map((c) => (c[0] as any)?.queryKey);
    expect(keys).toContainEqual(['entities']);
    expect(keys).toContainEqual(['records']);
  });

  it('useCreateField → invalidates [entities] AND [entities, entityId]', async () => {
    const entityId = 'eid';
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockEntity));
    const spy = vi.spyOn(queryClient, 'invalidateQueries');

    const field = { ...mockField, name: 'x' } as AnyField;
    const { result } = renderHook(() => useCreateField(), { wrapper: createWrapper() });
    await act(async () => { result.current.mutate({ entityId, field }); });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const keys = spy.mock.calls.map((c) => (c[0] as any)?.queryKey);
    expect(keys).toContainEqual(['entities']);
    expect(keys).toContainEqual(['entities', entityId]);
  });

  it('useDeleteField → invalidates [entities] AND [entities, entityId]', async () => {
    const entityId = 'eid2';
    const fieldId = 'fid2';
    mockedDel.mockResolvedValueOnce(createSuccessResponse(undefined));
    const spy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteField(), { wrapper: createWrapper() });
    await act(async () => { result.current.mutate({ entityId, fieldId }); });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const keys = spy.mock.calls.map((c) => (c[0] as any)?.queryKey);
    expect(keys).toContainEqual(['entities']);
    expect(keys).toContainEqual(['entities', entityId]);
  });

  it('useCreateRelation → invalidates [relations] AND [entities]', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockRelation));
    const spy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateRelation(), { wrapper: createWrapper() });
    await act(async () => { result.current.mutate(mockRelation); });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const keys = spy.mock.calls.map((c) => (c[0] as any)?.queryKey);
    expect(keys).toContainEqual(['relations']);
    expect(keys).toContainEqual(['entities']);
  });

  it('useUpdateRelation → invalidates [relations] AND [relations, id]', async () => {
    const id = 'rid';
    mockedPut.mockResolvedValueOnce(createSuccessResponse(mockRelation));
    const spy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateRelation(), { wrapper: createWrapper() });
    await act(async () => { result.current.mutate({ id, relation: { label: 'x' } }); });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const keys = spy.mock.calls.map((c) => (c[0] as any)?.queryKey);
    expect(keys).toContainEqual(['relations']);
    expect(keys).toContainEqual(['relations', id]);
  });

  it('useDeleteRelation → invalidates [relations] AND [entities]', async () => {
    mockedDel.mockResolvedValueOnce(createSuccessResponse(undefined));
    const spy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteRelation(), { wrapper: createWrapper() });
    await act(async () => { result.current.mutate('rid2'); });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const keys = spy.mock.calls.map((c) => (c[0] as any)?.queryKey);
    expect(keys).toContainEqual(['relations']);
    expect(keys).toContainEqual(['entities']);
  });
});
