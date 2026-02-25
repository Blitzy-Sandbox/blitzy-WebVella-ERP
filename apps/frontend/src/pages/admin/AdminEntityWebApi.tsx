/**
 * AdminEntityWebApi — Entity Web API Reference Page
 *
 * Replaces `WebVella.Erp.Plugins.SDK/Pages/entity/web-api.cshtml[.cs]`.
 * Displays REST endpoint documentation for a single entity, organised into
 * three groups — Meta (entity CRUD), Fields (field CRUD) and Records
 * (record CRUD).  Each group renders the HTTP method, URL, authorisation,
 * parameters, request/response body and a formatted JSON example.
 *
 * Route: `/admin/entities/:entityId/web-api`
 */

import { useState, useMemo, useCallback } from 'react';
import { useParams, Link } from 'react-router-dom';
import { useEntity } from '../../hooks/useEntities';
import { FieldType } from '../../types/entity';
import type { Field } from '../../types/entity';
import { TabNavRenderType } from '../../components/common/TabNav';
import Modal, { ModalSize } from '../../components/common/Modal';
import { get } from '../../api/client';
import Button, { ButtonColor } from '../../components/common/Button';

/* ------------------------------------------------------------------ */
/*  Constants                                                         */
/* ------------------------------------------------------------------ */

/** Entity admin sub-navigation entries matching the monolith's
 *  `AdminPageUtils.GetEntityAdminSubNav` toolbar. */
const ENTITY_SUB_NAV: ReadonlyArray<{
  id: string;
  label: string;
  pathSuffix: string;
}> = [
  { id: 'details', label: 'Details', pathSuffix: '' },
  { id: 'fields', label: 'Fields', pathSuffix: '/fields' },
  { id: 'relations', label: 'Relations', pathSuffix: '/relations' },
  { id: 'data', label: 'Data', pathSuffix: '/data' },
  { id: 'pages', label: 'Pages', pathSuffix: '/pages' },
  { id: 'web-api', label: 'Web API', pathSuffix: '/web-api' },
];

/** Definition of a single API-reference tab. */
interface ApiTabDef {
  readonly id: string;
  readonly label: string;
  readonly category: 'meta' | 'fields' | 'records';
}

/** All 11 active API documentation tabs (3 headers are rendered separately). */
const API_TABS: readonly ApiTabDef[] = [
  { id: 'meta-read', label: 'Read', category: 'meta' },
  { id: 'meta-update', label: 'Update / Patch', category: 'meta' },
  { id: 'meta-delete', label: 'Delete', category: 'meta' },
  { id: 'field-create', label: 'Create', category: 'fields' },
  { id: 'field-read', label: 'Read', category: 'fields' },
  { id: 'field-update', label: 'Update / Patch', category: 'fields' },
  { id: 'field-delete', label: 'Delete', category: 'fields' },
  { id: 'records-create', label: 'Create', category: 'records' },
  { id: 'records-query', label: 'Query', category: 'records' },
  { id: 'records-update', label: 'Update / Patch', category: 'records' },
  { id: 'records-delete', label: 'Delete', category: 'records' },
];

/* ------------------------------------------------------------------ */
/*  Presentation helpers                                              */
/* ------------------------------------------------------------------ */

/** Renders a `<pre>` code block with a dark background for JSON/URL
 *  samples.  Uses Tailwind utility classes exclusively. */
function CodeBlock({ children, label }: { children: string; label?: string }) {
  return (
    <div className="mb-4">
      {label && (
        <h6 className="mb-1 text-sm font-semibold text-gray-700">{label}</h6>
      )}
      <pre className="overflow-x-auto rounded-md bg-gray-900 p-4 text-sm leading-relaxed text-green-300">
        <code>{children}</code>
      </pre>
    </div>
  );
}

/** Renders a horizontal parameter-documentation table. */
function ParamTable({
  params,
}: {
  params: ReadonlyArray<{
    name: string;
    type: string;
    description: string;
    required?: boolean;
  }>;
}) {
  if (params.length === 0) return null;
  return (
    <div className="mb-4 overflow-x-auto">
      <table className="min-w-full divide-y divide-gray-200 border text-sm">
        <thead className="bg-gray-50">
          <tr>
            <th className="px-4 py-2 text-start font-medium text-gray-600">
              Name
            </th>
            <th className="px-4 py-2 text-start font-medium text-gray-600">
              Type
            </th>
            <th className="px-4 py-2 text-start font-medium text-gray-600">
              Required
            </th>
            <th className="px-4 py-2 text-start font-medium text-gray-600">
              Description
            </th>
          </tr>
        </thead>
        <tbody className="divide-y divide-gray-100">
          {params.map((p) => (
            <tr key={p.name}>
              <td className="px-4 py-2 font-mono text-xs text-blue-700">
                {p.name}
              </td>
              <td className="px-4 py-2 text-gray-600">{p.type}</td>
              <td className="px-4 py-2 text-gray-600">
                {p.required === false ? 'No' : 'Yes'}
              </td>
              <td className="px-4 py-2 text-gray-600">{p.description}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** Section heading inside a tab panel. */
function SectionHeading({ children }: { children: React.ReactNode }) {
  return (
    <h5 className="mb-2 mt-6 border-b border-gray-200 pb-1 text-base font-semibold text-gray-800">
      {children}
    </h5>
  );
}

/* ------------------------------------------------------------------ */
/*  Sidebar category headers (disabled pills)                         */
/* ------------------------------------------------------------------ */

const CATEGORY_LABELS: Record<string, string> = {
  meta: 'Meta',
  fields: 'Fields',
  records: 'Records',
};

/* ------------------------------------------------------------------ */
/*  Component                                                         */
/* ------------------------------------------------------------------ */

export default function AdminEntityWebApi() {
  /* --- Route params ------------------------------------------------ */
  const { entityId = '' } = useParams<{ entityId: string }>();

  /* --- Server state ------------------------------------------------ */
  const { data: entity, isLoading, isError, error } = useEntity(entityId);

  /* --- Local state ------------------------------------------------- */
  const [activeTab, setActiveTab] = useState<string>('meta-read');
  const [isModalVisible, setIsModalVisible] = useState(false);
  const [apiResponseData, setApiResponseData] = useState('');
  const [isRequestLoading, setIsRequestLoading] = useState(false);

  /**
   * The render type drives the sidebar styling.  We use the PILLS variant
   * to mirror the monolith's vertical `nav-pills` layout.
   */
  const renderType: string = TabNavRenderType.PILLS;

  /* --- Derived values ---------------------------------------------- */

  /** First field ID used in endpoint examples, matching the monolith's
   *  `SampleFieldId = ErpEntity.Fields[0].Id`. */
  const sampleField: Field | undefined = useMemo(
    () => (entity?.fields?.length ? entity.fields[0] : undefined),
    [entity],
  );
  const sampleFieldId = sampleField?.id ?? '00000000-0000-0000-0000-000000000000';

  /** Pre-computed API URL segments used across tab panels. */
  const apiUrls = useMemo(() => {
    const name = entity?.name ?? '{entityName}';
    const id = entity?.id ?? '{entityId}';
    return {
      /** Read entity by name or id */
      entityByName: `/v1/entities/${name}`,
      entityById: `/v1/entities/${id}`,
      /** Fields */
      fieldCreate: `/v1/entities/${id}/fields`,
      fieldUpdate: `/v1/entities/${id}/fields/${sampleFieldId}`,
      fieldDelete: `/v1/entities/${id}/fields/${sampleFieldId}`,
      /** Records */
      recordCreate: `/v1/entity-management/entities/${name}/records`,
      recordQuery: '/v1/entity-management/eql',
      recordUpdate: `/v1/entity-management/entities/${name}/records/{recordId}`,
      recordDelete: `/v1/entity-management/entities/${name}/records/{recordId}`,
    };
  }, [entity, sampleFieldId]);

  /** Build a compact sample record payload from entity fields for
   *  request/response JSON examples. */
  const sampleRecordPayload = useMemo(() => {
    if (!entity?.fields) return {};
    const payload: Record<string, unknown> = {};
    entity.fields.forEach((f: Field) => {
      const ft = f.fieldType;
      if (ft === FieldType.GuidField && f.name === 'id') {
        payload[f.name] = '00000000-0000-0000-0000-000000000000';
      } else if (
        ft === FieldType.TextField ||
        ft === FieldType.EmailField ||
        ft === FieldType.PhoneField ||
        ft === FieldType.UrlField
      ) {
        payload[f.name] = f.label ? `Sample ${f.label}` : '';
      } else if (
        ft === FieldType.NumberField ||
        ft === FieldType.CurrencyField ||
        ft === FieldType.PercentField ||
        ft === FieldType.AutoNumberField
      ) {
        payload[f.name] = 0;
      } else if (ft === FieldType.CheckboxField) {
        payload[f.name] = false;
      } else if (
        ft === FieldType.DateField ||
        ft === FieldType.DateTimeField
      ) {
        payload[f.name] = new Date().toISOString();
      } else if (ft === FieldType.MultiSelectField) {
        payload[f.name] = [];
      } else if (ft === FieldType.GuidField) {
        payload[f.name] = null;
      } else {
        payload[f.name] = null;
      }
    });
    return payload;
  }, [entity]);

  /** Example field creation payload. */
  const sampleFieldPayload = useMemo(
    () => ({
      id: '00000000-0000-0000-0000-000000000000',
      name: 'sample_field',
      label: 'Sample Field',
      fieldType: 'TextField',
      required: false,
      unique: false,
      searchable: false,
      system: false,
      maxLength: 200,
    }),
    [],
  );

  /* --- Event handlers ---------------------------------------------- */

  /** Calls the Entity Management API (GET by name) and shows the raw
   *  JSON response in a large modal — mirrors the monolith's jQuery
   *  `$.ajax` call in `web-api.cshtml`. */
  const handleShowRequestResult = useCallback(async () => {
    if (!entity?.name) return;
    setIsRequestLoading(true);
    try {
      const response = await get<unknown>(`/v1/entities/${entity.name}`);
      setApiResponseData(JSON.stringify(response, null, 2));
    } catch (err: unknown) {
      const errMsg =
        err instanceof Error ? err.message : JSON.stringify(err, null, 2);
      setApiResponseData(errMsg);
    } finally {
      setIsRequestLoading(false);
      setIsModalVisible(true);
    }
  }, [entity]);

  /* --- Tab content renderer ---------------------------------------- */

  /** Returns JSX for the active tab's documentation panel. */
  const tabContent = useMemo(() => {
    const entityName = entity?.name ?? '{entityName}';
    const eid = entity?.id ?? '{entityId}';

    const fieldsList = entity?.fields ?? [];
    const fieldSummary = fieldsList
      .slice(0, 5)
      .map((f: Field) => `"${f.name}": ${f.required ? '(required)' : '(optional)'} ${f.fieldType}`)
      .join('\n  ');

    switch (activeTab) {
      /* ----- META : READ ----- */
      case 'meta-read':
        return (
          <div>
            <SectionHeading>HTTP Request</SectionHeading>
            <CodeBlock label="Read entity meta by name">
              {`GET ${apiUrls.entityByName}`}
            </CodeBlock>
            <CodeBlock label="Read entity meta by ID">
              {`GET ${apiUrls.entityById}`}
            </CodeBlock>

            <SectionHeading>Authorization</SectionHeading>
            <p className="mb-4 text-sm text-gray-600">
              Bearer token required. Include <code className="text-blue-700">Authorization: Bearer &lt;jwt&gt;</code> header.
            </p>

            <SectionHeading>Route Parameters</SectionHeading>
            <ParamTable
              params={[
                {
                  name: 'entityName',
                  type: 'string',
                  description: `The name of the entity (e.g. "${entityName}")`,
                },
                {
                  name: 'entityId',
                  type: 'Guid',
                  description: `The unique identifier of the entity (e.g. "${eid}")`,
                  required: false,
                },
              ]}
            />

            <SectionHeading>Request Body</SectionHeading>
            <p className="mb-4 text-sm text-gray-600">Do not supply a request body.</p>

            <SectionHeading>Example Response</SectionHeading>
            <CodeBlock>
              {JSON.stringify(
                {
                  success: true,
                  timestamp: new Date().toISOString(),
                  object: {
                    id: eid,
                    name: entityName,
                    label: entity?.label ?? entityName,
                    labelPlural: entity?.labelPlural ?? entityName,
                    system: entity?.system ?? false,
                    iconName: entity?.iconName ?? 'fas fa-database',
                    color: entity?.color ?? '#2196F3',
                    fields: fieldsList.length > 0
                      ? `[...${fieldsList.length} fields]`
                      : '[]',
                  },
                },
                null,
                2,
              )}
            </CodeBlock>

            <div className="mt-4">
              <Button
                color={ButtonColor.Primary}
                onClick={handleShowRequestResult}
                isDisabled={isRequestLoading || !entity}
                text={isRequestLoading ? 'Loading…' : 'Show request result'}
                iconClass="fas fa-play"
              />
            </div>
          </div>
        );

      /* ----- META : UPDATE ----- */
      case 'meta-update':
        return (
          <div>
            <SectionHeading>HTTP Request</SectionHeading>
            <CodeBlock>{`PATCH ${apiUrls.entityById}`}</CodeBlock>

            <SectionHeading>Authorization</SectionHeading>
            <p className="mb-4 text-sm text-gray-600">
              Bearer token required. Include <code className="text-blue-700">Authorization: Bearer &lt;jwt&gt;</code> header.
            </p>

            <SectionHeading>Route Parameters</SectionHeading>
            <ParamTable
              params={[
                {
                  name: 'entityId',
                  type: 'Guid',
                  description: `The unique identifier of the entity (e.g. "${eid}")`,
                },
              ]}
            />

            <SectionHeading>Request Body</SectionHeading>
            <CodeBlock>
              {JSON.stringify(
                {
                  label: entity?.label ?? 'Updated Label',
                  labelPlural: entity?.labelPlural ?? 'Updated Labels',
                  iconName: entity?.iconName ?? 'fas fa-database',
                  color: entity?.color ?? '#2196F3',
                },
                null,
                2,
              )}
            </CodeBlock>

            <SectionHeading>Example Response</SectionHeading>
            <CodeBlock>
              {JSON.stringify(
                {
                  success: true,
                  message: 'Entity updated successfully',
                  timestamp: new Date().toISOString(),
                  object: {
                    id: eid,
                    name: entityName,
                    label: entity?.label ?? entityName,
                  },
                },
                null,
                2,
              )}
            </CodeBlock>
          </div>
        );

      /* ----- META : DELETE ----- */
      case 'meta-delete':
        return (
          <div>
            <SectionHeading>HTTP Request</SectionHeading>
            <CodeBlock>{`DELETE ${apiUrls.entityById}`}</CodeBlock>

            <SectionHeading>Authorization</SectionHeading>
            <p className="mb-4 text-sm text-gray-600">
              Bearer token required. Include <code className="text-blue-700">Authorization: Bearer &lt;jwt&gt;</code> header.
            </p>

            <SectionHeading>Route Parameters</SectionHeading>
            <ParamTable
              params={[
                {
                  name: 'entityId',
                  type: 'Guid',
                  description: `The unique identifier of the entity (e.g. "${eid}")`,
                },
              ]}
            />

            <SectionHeading>Request Body</SectionHeading>
            <p className="mb-4 text-sm text-gray-600">Do not supply a request body.</p>

            <SectionHeading>Example Response</SectionHeading>
            <CodeBlock>
              {JSON.stringify(
                {
                  success: true,
                  message: 'Entity deleted successfully',
                  timestamp: new Date().toISOString(),
                },
                null,
                2,
              )}
            </CodeBlock>
          </div>
        );

      /* ----- FIELD : CREATE ----- */
      case 'field-create':
        return (
          <div>
            <SectionHeading>HTTP Request</SectionHeading>
            <CodeBlock>{`POST ${apiUrls.fieldCreate}`}</CodeBlock>

            <SectionHeading>Authorization</SectionHeading>
            <p className="mb-4 text-sm text-gray-600">
              Bearer token required. Include <code className="text-blue-700">Authorization: Bearer &lt;jwt&gt;</code> header.
            </p>

            <SectionHeading>Route Parameters</SectionHeading>
            <ParamTable
              params={[
                {
                  name: 'entityId',
                  type: 'Guid',
                  description: `The unique identifier of the entity (e.g. "${eid}")`,
                },
              ]}
            />

            <SectionHeading>Request Body</SectionHeading>
            <CodeBlock>{JSON.stringify(sampleFieldPayload, null, 2)}</CodeBlock>

            <SectionHeading>Example Response</SectionHeading>
            <CodeBlock>
              {JSON.stringify(
                {
                  success: true,
                  message: 'Field created successfully',
                  timestamp: new Date().toISOString(),
                  object: sampleFieldPayload,
                },
                null,
                2,
              )}
            </CodeBlock>
          </div>
        );

      /* ----- FIELD : READ ----- */
      case 'field-read':
        return (
          <div>
            <div className="rounded-md border border-blue-200 bg-blue-50 p-4">
              <h4 className="mb-1 text-base font-semibold text-blue-800">
                Important Note
              </h4>
              <p className="text-sm text-blue-700">
                Field metadata is accessible only through the entity meta read
                endpoint. For more information, switch to the{' '}
                <button
                  type="button"
                  className="font-medium text-blue-800 underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
                  onClick={() => setActiveTab('meta-read')}
                >
                  Meta &raquo; Read
                </button>{' '}
                tab.
              </p>
            </div>

            <SectionHeading>Alternative — Individual Field</SectionHeading>
            <CodeBlock>{`GET /v1/entities/${eid}/fields/${sampleFieldId}`}</CodeBlock>

            <SectionHeading>Fields Summary</SectionHeading>
            {fieldsList.length > 0 ? (
              <div className="overflow-x-auto">
                <table className="min-w-full divide-y divide-gray-200 border text-sm">
                  <thead className="bg-gray-50">
                    <tr>
                      <th className="px-4 py-2 text-start font-medium text-gray-600">Name</th>
                      <th className="px-4 py-2 text-start font-medium text-gray-600">Label</th>
                      <th className="px-4 py-2 text-start font-medium text-gray-600">Type</th>
                      <th className="px-4 py-2 text-start font-medium text-gray-600">Required</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-gray-100">
                    {fieldsList.map((f: Field) => (
                      <tr key={f.id}>
                        <td className="px-4 py-2 font-mono text-xs text-blue-700">{f.name}</td>
                        <td className="px-4 py-2 text-gray-600">{f.label}</td>
                        <td className="px-4 py-2 text-gray-600">{f.fieldType}</td>
                        <td className="px-4 py-2 text-gray-600">{f.required ? 'Yes' : 'No'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : (
              <p className="text-sm text-gray-500">No fields defined for this entity.</p>
            )}
          </div>
        );

      /* ----- FIELD : UPDATE ----- */
      case 'field-update':
        return (
          <div>
            <SectionHeading>HTTP Request</SectionHeading>
            <CodeBlock>{`PATCH ${apiUrls.fieldUpdate}`}</CodeBlock>

            <SectionHeading>Authorization</SectionHeading>
            <p className="mb-4 text-sm text-gray-600">
              Bearer token required. Include <code className="text-blue-700">Authorization: Bearer &lt;jwt&gt;</code> header.
            </p>

            <SectionHeading>Route Parameters</SectionHeading>
            <ParamTable
              params={[
                {
                  name: 'entityId',
                  type: 'Guid',
                  description: `The unique identifier of the entity (e.g. "${eid}")`,
                },
                {
                  name: 'fieldId',
                  type: 'Guid',
                  description: `The unique identifier of the field (e.g. "${sampleFieldId}")`,
                },
              ]}
            />

            <SectionHeading>Request Body</SectionHeading>
            <CodeBlock>
              {JSON.stringify(
                {
                  label: 'Updated Label',
                  required: true,
                  searchable: true,
                },
                null,
                2,
              )}
            </CodeBlock>

            <SectionHeading>Example Response</SectionHeading>
            <CodeBlock>
              {JSON.stringify(
                {
                  success: true,
                  message: 'Field updated successfully',
                  timestamp: new Date().toISOString(),
                },
                null,
                2,
              )}
            </CodeBlock>
          </div>
        );

      /* ----- FIELD : DELETE ----- */
      case 'field-delete':
        return (
          <div>
            <SectionHeading>HTTP Request</SectionHeading>
            <CodeBlock>{`DELETE ${apiUrls.fieldDelete}`}</CodeBlock>

            <SectionHeading>Authorization</SectionHeading>
            <p className="mb-4 text-sm text-gray-600">
              Bearer token required. Include <code className="text-blue-700">Authorization: Bearer &lt;jwt&gt;</code> header.
            </p>

            <SectionHeading>Route Parameters</SectionHeading>
            <ParamTable
              params={[
                {
                  name: 'entityId',
                  type: 'Guid',
                  description: `The unique identifier of the entity (e.g. "${eid}")`,
                },
                {
                  name: 'fieldId',
                  type: 'Guid',
                  description: `The unique identifier of the field (e.g. "${sampleFieldId}")`,
                },
              ]}
            />

            <SectionHeading>Request Body</SectionHeading>
            <p className="mb-4 text-sm text-gray-600">Do not supply a request body.</p>

            <SectionHeading>Example Response</SectionHeading>
            <CodeBlock>
              {JSON.stringify(
                {
                  success: true,
                  message: 'Field deleted successfully',
                  timestamp: new Date().toISOString(),
                },
                null,
                2,
              )}
            </CodeBlock>
          </div>
        );

      /* ----- RECORDS : CREATE ----- */
      case 'records-create':
        return (
          <div>
            <SectionHeading>HTTP Request</SectionHeading>
            <CodeBlock>{`POST ${apiUrls.recordCreate}`}</CodeBlock>

            <SectionHeading>Authorization</SectionHeading>
            <p className="mb-4 text-sm text-gray-600">
              Bearer token required. Include <code className="text-blue-700">Authorization: Bearer &lt;jwt&gt;</code> header.
            </p>

            <SectionHeading>Route Parameters</SectionHeading>
            <ParamTable
              params={[
                {
                  name: 'entityName',
                  type: 'string',
                  description: `The name of the entity (e.g. "${entityName}")`,
                },
              ]}
            />

            <SectionHeading>Request Body</SectionHeading>
            <p className="mb-2 text-sm text-gray-600">
              Supply a JSON object with the entity&apos;s field values:
            </p>
            {fieldSummary ? (
              <CodeBlock>
                {`{\n  ${fieldSummary}\n  ...\n}`}
              </CodeBlock>
            ) : (
              <CodeBlock>{JSON.stringify(sampleRecordPayload, null, 2)}</CodeBlock>
            )}

            <SectionHeading>Example Response</SectionHeading>
            <CodeBlock>
              {JSON.stringify(
                {
                  success: true,
                  message: 'Record created successfully',
                  timestamp: new Date().toISOString(),
                  object: sampleRecordPayload,
                },
                null,
                2,
              )}
            </CodeBlock>
          </div>
        );

      /* ----- RECORDS : QUERY ----- */
      case 'records-query':
        return (
          <div>
            <SectionHeading>HTTP Request</SectionHeading>
            <CodeBlock>{`POST ${apiUrls.recordQuery}`}</CodeBlock>

            <SectionHeading>Authorization</SectionHeading>
            <p className="mb-4 text-sm text-gray-600">
              Bearer token required. Include <code className="text-blue-700">Authorization: Bearer &lt;jwt&gt;</code> header.
            </p>

            <SectionHeading>Request Body</SectionHeading>
            <CodeBlock>
              {JSON.stringify(
                {
                  eql: `SELECT * FROM ${entityName} WHERE id = '00000000-0000-0000-0000-000000000000'`,
                  parameters: [],
                },
                null,
                2,
              )}
            </CodeBlock>

            <SectionHeading>EQL Syntax Reference</SectionHeading>
            <div className="mb-4 rounded-md border border-gray-200 bg-gray-50 p-4 text-sm text-gray-700">
              <p className="mb-2">
                The Entity Query Language (EQL) supports a SQL-like syntax:
              </p>
              <ul className="list-inside list-disc space-y-1">
                <li>
                  <code className="text-blue-700">SELECT field1, field2 FROM {entityName}</code>
                </li>
                <li>
                  <code className="text-blue-700">WHERE field1 = &apos;value&apos;</code>
                </li>
                <li>
                  <code className="text-blue-700">ORDER BY field1 ASC</code>
                </li>
                <li>
                  <code className="text-blue-700">PAGE 1 PAGESIZE 10</code>
                </li>
              </ul>
            </div>

            <SectionHeading>Example Response</SectionHeading>
            <CodeBlock>
              {JSON.stringify(
                {
                  success: true,
                  timestamp: new Date().toISOString(),
                  object: {
                    list: [sampleRecordPayload],
                    totalCount: 1,
                  },
                },
                null,
                2,
              )}
            </CodeBlock>
          </div>
        );

      /* ----- RECORDS : UPDATE ----- */
      case 'records-update':
        return (
          <div>
            <SectionHeading>HTTP Request</SectionHeading>
            <CodeBlock>{`PUT ${apiUrls.recordUpdate}`}</CodeBlock>

            <SectionHeading>Authorization</SectionHeading>
            <p className="mb-4 text-sm text-gray-600">
              Bearer token required. Include <code className="text-blue-700">Authorization: Bearer &lt;jwt&gt;</code> header.
            </p>

            <SectionHeading>Route Parameters</SectionHeading>
            <ParamTable
              params={[
                {
                  name: 'entityName',
                  type: 'string',
                  description: `The name of the entity (e.g. "${entityName}")`,
                },
                {
                  name: 'recordId',
                  type: 'Guid',
                  description: 'The unique identifier of the record',
                },
              ]}
            />

            <SectionHeading>Request Body</SectionHeading>
            <p className="mb-2 text-sm text-gray-600">
              Supply a JSON object with the updated field values:
            </p>
            <CodeBlock>{JSON.stringify(sampleRecordPayload, null, 2)}</CodeBlock>

            <SectionHeading>Example Response</SectionHeading>
            <CodeBlock>
              {JSON.stringify(
                {
                  success: true,
                  message: 'Record updated successfully',
                  timestamp: new Date().toISOString(),
                  object: sampleRecordPayload,
                },
                null,
                2,
              )}
            </CodeBlock>
          </div>
        );

      /* ----- RECORDS : DELETE ----- */
      case 'records-delete':
        return (
          <div>
            <SectionHeading>HTTP Request</SectionHeading>
            <CodeBlock>{`DELETE ${apiUrls.recordDelete}`}</CodeBlock>

            <SectionHeading>Authorization</SectionHeading>
            <p className="mb-4 text-sm text-gray-600">
              Bearer token required. Include <code className="text-blue-700">Authorization: Bearer &lt;jwt&gt;</code> header.
            </p>

            <SectionHeading>Route Parameters</SectionHeading>
            <ParamTable
              params={[
                {
                  name: 'entityName',
                  type: 'string',
                  description: `The name of the entity (e.g. "${entityName}")`,
                },
                {
                  name: 'recordId',
                  type: 'Guid',
                  description: 'The unique identifier of the record to delete',
                },
              ]}
            />

            <SectionHeading>Request Body</SectionHeading>
            <p className="mb-4 text-sm text-gray-600">Do not supply a request body.</p>

            <SectionHeading>Example Response</SectionHeading>
            <CodeBlock>
              {JSON.stringify(
                {
                  success: true,
                  message: 'Record deleted successfully',
                  timestamp: new Date().toISOString(),
                },
                null,
                2,
              )}
            </CodeBlock>
          </div>
        );

      default:
        return (
          <p className="text-sm text-gray-500">
            Select a tab from the sidebar to view API documentation.
          </p>
        );
    }
  }, [
    activeTab,
    entity,
    apiUrls,
    sampleFieldId,
    sampleFieldPayload,
    sampleRecordPayload,
    handleShowRequestResult,
    isRequestLoading,
  ]);

  /* ---------------------------------------------------------------- */
  /*  Loading / Error states                                          */
  /* ---------------------------------------------------------------- */

  if (isLoading) {
    return (
      <div className="flex min-h-[40vh] items-center justify-center">
        <p className="text-gray-500">Loading entity data…</p>
      </div>
    );
  }

  if (isError) {
    return (
      <div className="mx-auto max-w-3xl py-10">
        <div className="rounded-md border border-red-200 bg-red-50 p-4 text-sm text-red-700">
          <strong>Error: </strong>
          {error instanceof Error ? error.message : 'Failed to load entity.'}
        </div>
      </div>
    );
  }

  if (!entity) {
    return (
      <div className="mx-auto max-w-3xl py-10">
        <div className="rounded-md border border-yellow-200 bg-yellow-50 p-4 text-sm text-yellow-800">
          Entity not found.
        </div>
      </div>
    );
  }

  /* ---------------------------------------------------------------- */
  /*  Render                                                          */
  /* ---------------------------------------------------------------- */

  const entityBasePath = `/admin/entities/${entityId}`;

  return (
    <div className="min-h-screen bg-gray-50">
      {/* ---- Page header ---- */}
      <header className="border-b border-gray-200 bg-white px-6 py-4">
        <div className="flex flex-wrap items-center gap-3">
          {entity.iconName && (
            <span
              className="flex h-8 w-8 items-center justify-center rounded text-white"
              style={entity.color ? { backgroundColor: entity.color } : undefined}
              aria-hidden="true"
            >
              <i className={entity.iconName} />
            </span>
          )}
          <h1 className="text-xl font-semibold text-gray-900">
            {entity.label || entity.name} — Web API
          </h1>
        </div>

        {/* Entity admin sub-nav */}
        <nav
          className="mt-3 flex flex-wrap gap-1"
          aria-label="Entity administration"
        >
          {ENTITY_SUB_NAV.map((item) => {
            const isActive = item.id === 'web-api';
            return (
              <Link
                key={item.id}
                to={`${entityBasePath}${item.pathSuffix}`}
                className={`rounded-md px-3 py-1.5 text-sm font-medium transition-colors ${
                  isActive
                    ? 'bg-blue-600 text-white'
                    : 'text-gray-600 hover:bg-gray-100 hover:text-gray-900'
                }`}
                aria-current={isActive ? 'page' : undefined}
              >
                {item.label}
              </Link>
            );
          })}
        </nav>
      </header>

      {/* ---- Two-column layout: sidebar + content ---- */}
      <div className="flex gap-0">
        {/* Sidebar — vertical pills navigation */}
        <aside
          className="w-52 shrink-0 border-e border-gray-200 bg-white"
          role="tablist"
          aria-orientation="vertical"
          aria-label="API endpoint documentation"
          data-render-type={renderType}
        >
          <div className="sticky top-0 space-y-0.5 p-3">
            {(['meta', 'fields', 'records'] as const).map((cat) => (
              <div key={cat}>
                {/* Category header (non-interactive) */}
                <span className="mt-3 block px-3 py-1.5 text-xs font-bold uppercase tracking-wider text-gray-400 first:mt-0">
                  {CATEGORY_LABELS[cat]}
                </span>
                {/* Tabs in this category */}
                {API_TABS.filter((t) => t.category === cat).map((tab) => {
                  const isActive = activeTab === tab.id;
                  return (
                    <button
                      key={tab.id}
                      type="button"
                      role="tab"
                      id={`tab-${tab.id}`}
                      aria-selected={isActive}
                      aria-controls={`panel-${tab.id}`}
                      className={`block w-full rounded-md px-3 py-1.5 text-start text-sm transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 ${
                        isActive
                          ? 'bg-blue-600 font-medium text-white'
                          : 'text-gray-700 hover:bg-gray-100 hover:text-gray-900'
                      }`}
                      onClick={() => setActiveTab(tab.id)}
                    >
                      {tab.label}
                    </button>
                  );
                })}
              </div>
            ))}
          </div>
        </aside>

        {/* Content area — active tab panel */}
        <main
          className="min-w-0 flex-1 p-6"
          role="tabpanel"
          id={`panel-${activeTab}`}
          aria-labelledby={`tab-${activeTab}`}
        >
          <div className="mx-auto max-w-4xl rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
            {tabContent}
          </div>
        </main>
      </div>

      {/* ---- API response modal ---- */}
      <Modal
        isVisible={isModalVisible}
        title="API Response"
        size={ModalSize.Large}
        onClose={() => setIsModalVisible(false)}
      >
        <pre className="max-h-[60vh] overflow-auto rounded-md bg-gray-900 p-4 text-sm leading-relaxed text-green-300">
          <code>{apiResponseData || 'No data'}</code>
        </pre>
      </Modal>
    </div>
  );
}
