#!/usr/bin/env node
/**
 * E2E Mock API Server for Playwright Tests
 *
 * Provides mock responses for Cognito authentication and all API Gateway
 * endpoints. Required because LocalStack Community Edition does not include
 * API Gateway v2 or Cognito services.
 *
 * Usage: node tools/scripts/e2e-mock-server.mjs [--port 3456]
 */

import http from 'node:http';
import crypto from 'node:crypto';

const PORT = parseInt(process.env.MOCK_PORT || '3456', 10);

// ---------------------------------------------------------------------------
// Mock Data Store (in-memory)
// ---------------------------------------------------------------------------

const TEST_USER = {
  sub: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890',
  email: 'erp@webvella.com',
  password: 'erpadmin',
  roles: ['administrator'],
  firstName: 'System',
  lastName: 'Administrator',
};

// Secondary test user used by files.spec.ts and notifications.spec.ts
const TEST_USER_ALT = {
  sub: 'b2c3d4e5-f6a7-8901-bcde-f12345678901',
  email: 'testuser@webvella.com',
  password: 'TestPass123!',
  roles: ['administrator'],
  firstName: 'Test',
  lastName: 'User',
};

// Tokens
function generateToken() {
  const header = Buffer.from(JSON.stringify({ alg: 'HS256', typ: 'JWT' })).toString('base64url');
  const payload = Buffer.from(JSON.stringify({
    sub: TEST_USER.sub,
    email: TEST_USER.email,
    'cognito:groups': TEST_USER.roles,
    iss: 'https://cognito-idp.us-east-1.amazonaws.com/us-east-1_mock',
    aud: 'mock-client-id',
    token_use: 'access',
    exp: Math.floor(Date.now() / 1000) + 3600,
    iat: Math.floor(Date.now() / 1000),
    email_verified: true,
  })).toString('base64url');
  const sig = crypto.randomBytes(32).toString('base64url');
  return `${header}.${payload}.${sig}`;
}

function generateIdToken() {
  const header = Buffer.from(JSON.stringify({ alg: 'HS256', typ: 'JWT' })).toString('base64url');
  const payload = Buffer.from(JSON.stringify({
    sub: TEST_USER.sub,
    email: TEST_USER.email,
    email_verified: true,
    'cognito:groups': TEST_USER.roles,
    'cognito:username': TEST_USER.sub,
    iss: 'https://cognito-idp.us-east-1.amazonaws.com/us-east-1_mock',
    aud: 'mock-client-id',
    token_use: 'id',
    exp: Math.floor(Date.now() / 1000) + 3600,
    iat: Math.floor(Date.now() / 1000),
    given_name: TEST_USER.firstName,
    family_name: TEST_USER.lastName,
  })).toString('base64url');
  const sig = crypto.randomBytes(32).toString('base64url');
  return `${header}.${payload}.${sig}`;
}

// In-memory data stores
const entities = new Map();
const records = new Map(); // entityName -> Map<id, record>
const accounts = new Map();
const contacts = new Map();
const roles = new Map();
const users = new Map();
const files = new Map();
const emails = new Map();
const projects = new Map();
const tasks = new Map();
const timelogs = new Map();
const comments = new Map();
const plugins = new Map();
const workflows = new Map();
const reports = new Map();

// Seed default data
function seedData() {
  // Seed system roles
  roles.set('a1b2c3d4-0000-0000-0000-000000000001', { id: 'a1b2c3d4-0000-0000-0000-000000000001', name: 'administrator', description: 'System Administrator Role', is_system: true });
  roles.set('a1b2c3d4-0000-0000-0000-000000000002', { id: 'a1b2c3d4-0000-0000-0000-000000000002', name: 'regular', description: 'Regular User Role', is_system: true });
  roles.set('a1b2c3d4-0000-0000-0000-000000000003', { id: 'a1b2c3d4-0000-0000-0000-000000000003', name: 'guest', description: 'Guest Role', is_system: true });

  // Seed system user
  users.set(TEST_USER.sub, {
    id: TEST_USER.sub,
    email: TEST_USER.email,
    first_name: TEST_USER.firstName,
    last_name: TEST_USER.lastName,
    enabled: true,
    roles: ['administrator'],
    created_on: new Date().toISOString(),
  });

  // Seed alt test user (files.spec.ts, notifications.spec.ts)
  users.set(TEST_USER_ALT.sub, {
    id: TEST_USER_ALT.sub,
    email: TEST_USER_ALT.email,
    first_name: TEST_USER_ALT.firstName,
    last_name: TEST_USER_ALT.lastName,
    enabled: true,
    roles: ['administrator'],
    created_on: new Date().toISOString(),
  });

  // Seed a test entity (for records E2E)
  const testEntityId = crypto.randomUUID();
  entities.set(testEntityId, {
    id: testEntityId,
    name: 'test_entity',
    label: 'Test Entity',
    label_plural: 'Test Entities',
    system: false,
    icon_name: 'database',
    record_permissions: { can_read: [], can_create: [], can_update: [], can_delete: [] },
    fields: [
      { id: crypto.randomUUID(), name: 'id', label: 'ID', fieldType: 16, field_type: 16, required: true, unique: true, system: true },
      { id: crypto.randomUUID(), name: 'subject', label: 'Subject', fieldType: 18, field_type: 18, required: true, unique: false, system: false },
      { id: crypto.randomUUID(), name: 'description', label: 'Description', fieldType: 11, field_type: 11, required: false, unique: false, system: false },
      { id: crypto.randomUUID(), name: 'status', label: 'Status', fieldType: 17, field_type: 17, required: false, unique: false, system: false },
    ],
    relations: [],
    created_on: new Date().toISOString(),
  });

  // Seed some records for the test entity
  records.set('test_entity', new Map());
  for (let i = 1; i <= 25; i++) {
    const recId = crypto.randomUUID();
    records.get('test_entity').set(recId, {
      id: recId,
      subject: `Test Record ${i}`,
      description: `Description for record ${i}`,
      status: i % 3 === 0 ? 'completed' : i % 2 === 0 ? 'in_progress' : 'open',
      created_on: new Date(Date.now() - i * 86400000).toISOString(),
    });
  }

  // Seed accounts
  for (let i = 1; i <= 5; i++) {
    const accId = crypto.randomUUID();
    accounts.set(accId, {
      id: accId,
      name: `Acme Corp ${i}`,
      email: `info${i}@acmecorp.example.com`,
      phone: `+1-555-010${i}`,
      website: `https://acmecorp${i}.example.com`,
      type: i % 2 === 0 ? 'customer' : 'prospect',
      industry: 'Technology',
      notes: `Seeded test account ${i}`,
      created_on: new Date(Date.now() - i * 86400000).toISOString(),
    });
  }

  // Seed contacts
  for (let i = 1; i <= 5; i++) {
    const cId = crypto.randomUUID();
    const accKeys = [...accounts.keys()];
    contacts.set(cId, {
      id: cId,
      first_name: `Contact`,
      last_name: `Person ${i}`,
      email: `contact${i}@example.com`,
      phone: `+1-555-020${i}`,
      salutation: i % 2 === 0 ? 'Mr.' : 'Ms.',
      account_id: accKeys[i % accKeys.length],
      created_on: new Date(Date.now() - i * 86400000).toISOString(),
    });
  }

  // Seed a project
  const projId = crypto.randomUUID();
  projects.set(projId, {
    id: projId,
    name: 'E2E Test Project',
    description: 'Project for E2E testing',
    status: 'active',
    start_date: new Date().toISOString(),
    created_on: new Date().toISOString(),
  });

  // Seed tasks
  for (let i = 1; i <= 5; i++) {
    const taskId = crypto.randomUUID();
    tasks.set(taskId, {
      id: taskId,
      project_id: projId,
      subject: `Task ${i}`,
      description: `Task description ${i}`,
      status: i % 2 === 0 ? 'completed' : 'open',
      priority: i % 3 === 0 ? 'high' : 'normal',
      created_on: new Date(Date.now() - i * 86400000).toISOString(),
    });
  }

  // Seed emails
  for (let i = 1; i <= 5; i++) {
    const emId = crypto.randomUUID();
    emails.set(emId, {
      id: emId,
      sender: 'system@webvella.com',
      recipient_email: `user${i}@example.com`,
      subject: `Test Email ${i}`,
      content_text: `Test email body ${i}`,
      content_html: `<p>Test email body ${i}</p>`,
      status: i % 3 === 0 ? 'sent' : i % 2 === 0 ? 'pending' : 'draft',
      priority: i % 3 === 0 ? 'high' : 'normal',
      created_on: new Date(Date.now() - i * 86400000).toISOString(),
      sent_on: i % 3 === 0 ? new Date(Date.now() - i * 86400000 + 3600000).toISOString() : null,
    });
  }

  // Seed plugins
  plugins.set('sdk-plugin', { id: 'sdk-plugin', name: 'SDK Plugin', version: '1.0.0', status: 'active', type: 'system' });
  plugins.set('crm-plugin', { id: 'crm-plugin', name: 'CRM Plugin', version: '1.0.0', status: 'active', type: 'business' });

  // Seed an app for navigation
  const appId = crypto.randomUUID();
  const areaId = crypto.randomUUID();
  const nodeId = crypto.randomUUID();
  global.__apps = [
    {
      id: appId,
      name: 'crm',
      label: 'CRM',
      description: 'Customer Relationship Management',
      icon_name: 'people',
      color: '#2196F3',
      weight: 1,
      access: [],
      home_pages: [],
      sitemap: {
        areas: [
          {
            id: areaId,
            name: 'contacts',
            label: 'Contacts',
            weight: 1,
            icon_name: 'contacts',
            nodes: [
              { id: nodeId, name: 'accounts', label: 'Accounts', weight: 1, icon_name: 'business', iconClass: 'fa fa-business', url: null, pages: [] },
              { id: crypto.randomUUID(), name: 'contacts', label: 'Contacts', weight: 2, icon_name: 'person', iconClass: 'fa fa-person', url: null, pages: [] },
              { id: crypto.randomUUID(), name: 'list', label: 'All Contacts', weight: 3, icon_name: 'list', iconClass: 'fa fa-list', url: '/crm/contacts/list', pages: [] },
            ],
          },
        ],
      },
    },
    {
      id: crypto.randomUUID(),
      name: 'projects',
      label: 'Projects',
      description: 'Project Management',
      icon_name: 'assignment',
      color: '#4CAF50',
      weight: 2,
      access: [],
      home_pages: [],
      sitemap: { areas: [] },
    },
  ];
}

seedData();

// ---------------------------------------------------------------------------
// Response Helpers
// ---------------------------------------------------------------------------

function jsonResponse(res, statusCode, data) {
  res.writeHead(statusCode, {
    'Content-Type': 'application/json',
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Methods': 'GET,POST,PUT,PATCH,DELETE,OPTIONS',
    'Access-Control-Allow-Headers': '*',
  });
  res.end(JSON.stringify(data));
}

function apiEnvelope(data, success = true, message = '', statusCode = 200) {
  return {
    success,
    errors: success ? [] : [{ key: 'general', value: '', message: message || 'An error occurred' }],
    statusCode,
    timestamp: new Date().toISOString(),
    message: message || (success ? 'Success' : 'Error'),
    object: data,
  };
}

function parseBody(req) {
  return new Promise((resolve) => {
    const chunks = [];
    req.on('data', (c) => chunks.push(c));
    req.on('end', () => {
      const raw = Buffer.concat(chunks).toString();
      try { resolve(JSON.parse(raw)); } catch { resolve(raw); }
    });
  });
}

// ---------------------------------------------------------------------------
// Cognito Handler
// ---------------------------------------------------------------------------

function handleCognito(req, res, body) {
  const target = req.headers['x-amz-target'] || '';

  if (target.includes('InitiateAuth')) {
    const params = typeof body === 'object' ? body : {};
    const authParams = params.AuthParameters || {};
    const username = authParams.USERNAME || '';
    const password = authParams.PASSWORD || '';
    const authFlow = params.AuthFlow || '';

    // Handle REFRESH_TOKEN_AUTH
    if (authFlow === 'REFRESH_TOKEN_AUTH' || authFlow === 'REFRESH_TOKEN') {
      return jsonResponse(res, 200, {
        AuthenticationResult: {
          AccessToken: generateToken(),
          IdToken: generateIdToken(),
          ExpiresIn: 3600,
          TokenType: 'Bearer',
        },
      });
    }

    // Handle USER_PASSWORD_AUTH
    const matchedUser = [TEST_USER, TEST_USER_ALT].find(u => u.email === username && u.password === password);
    if (matchedUser) {
      return jsonResponse(res, 200, {
        AuthenticationResult: {
          AccessToken: generateToken(),
          IdToken: generateIdToken(),
          RefreshToken: crypto.randomBytes(64).toString('base64url'),
          ExpiresIn: 3600,
          TokenType: 'Bearer',
        },
      });
    }

    // Check if user was dynamically created
    const dynUser = [...users.values()].find(u => u.email === username);
    if (dynUser && password) {
      return jsonResponse(res, 200, {
        AuthenticationResult: {
          AccessToken: generateToken(),
          IdToken: generateIdToken(),
          RefreshToken: crypto.randomBytes(64).toString('base64url'),
          ExpiresIn: 3600,
          TokenType: 'Bearer',
        },
      });
    }

    // Invalid credentials
    return jsonResponse(res, 400, {
      __type: 'NotAuthorizedException',
      message: 'Incorrect username or password.',
    });
  }

  if (target.includes('GetUser')) {
    return jsonResponse(res, 200, {
      Username: TEST_USER.sub,
      UserAttributes: [
        { Name: 'sub', Value: TEST_USER.sub },
        { Name: 'email', Value: TEST_USER.email },
        { Name: 'email_verified', Value: 'true' },
        { Name: 'given_name', Value: TEST_USER.firstName },
        { Name: 'family_name', Value: TEST_USER.lastName },
      ],
    });
  }

  if (target.includes('GlobalSignOut')) {
    return jsonResponse(res, 200, {});
  }

  // Default: return 200 empty
  return jsonResponse(res, 200, {});
}

// ---------------------------------------------------------------------------
// API Route Handler
// ---------------------------------------------------------------------------

function handleApi(req, res, body, pathname, method) {
  // Strip /v1 prefix for matching
  const path = pathname.replace(/^\/v1/, '');

  // --- AUTH ---
  if (path === '/auth/login' && method === 'POST') {
    return jsonResponse(res, 200, apiEnvelope({ token: generateToken(), user: { id: TEST_USER.sub, email: TEST_USER.email } }));
  }
  if (path === '/auth/me') {
    return jsonResponse(res, 200, apiEnvelope({ id: TEST_USER.sub, email: TEST_USER.email, first_name: TEST_USER.firstName, last_name: TEST_USER.lastName, roles: TEST_USER.roles }));
  }
  if (path === '/auth/logout' || path === '/auth/signout') {
    return jsonResponse(res, 200, apiEnvelope(null, true, 'Logged out'));
  }

  // --- USERS ---
  if (path === '/users' && method === 'GET') {
    return jsonResponse(res, 200, apiEnvelope([...users.values()]));
  }
  if (path === '/users' && method === 'POST') {
    const id = crypto.randomUUID();
    const user = { id, ...body, created_on: new Date().toISOString() };
    users.set(id, user);
    return jsonResponse(res, 200, apiEnvelope(user));
  }
  const userMatch = path.match(/^\/users\/([^/]+)$/);
  if (userMatch) {
    const uid = userMatch[1];
    if (method === 'GET') {
      const u = users.get(uid) || [...users.values()].find(x => x.email === uid);
      return u ? jsonResponse(res, 200, apiEnvelope(u)) : jsonResponse(res, 404, apiEnvelope(null, false, 'Not found', 404));
    }
    if (method === 'PUT' || method === 'PATCH') {
      const existing = users.get(uid);
      if (existing) { Object.assign(existing, body); users.set(uid, existing); }
      return jsonResponse(res, 200, apiEnvelope(existing || body));
    }
    if (method === 'DELETE') {
      users.delete(uid);
      return jsonResponse(res, 200, apiEnvelope(null, true, 'Deleted'));
    }
  }

  // --- ROLES ---
  if (path === '/roles' && method === 'GET') {
    return jsonResponse(res, 200, apiEnvelope([...roles.values()]));
  }
  if (path === '/roles' && method === 'POST') {
    const id = crypto.randomUUID();
    const role = { id, ...body, is_system: false, created_on: new Date().toISOString() };
    roles.set(id, role);
    return jsonResponse(res, 200, apiEnvelope(role));
  }
  const roleMatch = path.match(/^\/roles\/([^/]+)$/);
  if (roleMatch) {
    const rid = roleMatch[1];
    if (method === 'GET') {
      const r = roles.get(rid) || [...roles.values()].find(x => x.name === rid);
      return r ? jsonResponse(res, 200, apiEnvelope(r)) : jsonResponse(res, 404, apiEnvelope(null, false, 'Not found', 404));
    }
    if (method === 'PUT' || method === 'PATCH') {
      const existing = roles.get(rid);
      if (existing) { Object.assign(existing, body); roles.set(rid, existing); }
      return jsonResponse(res, 200, apiEnvelope(existing || body));
    }
    if (method === 'DELETE') {
      roles.delete(rid);
      return jsonResponse(res, 200, apiEnvelope(null, true, 'Deleted'));
    }
  }

  // --- ENTITIES (meta) ---
  if ((path === '/meta/entity' || path === '/entities') && method === 'GET') {
    return jsonResponse(res, 200, apiEnvelope([...entities.values()]));
  }
  if ((path === '/meta/entity' || path === '/entities') && method === 'POST') {
    const id = body.id || crypto.randomUUID();
    // Auto-create system 'id' GUID field if not present (mirrors EntityManager behavior)
    const fields = body.fields || [];
    if (!fields.find(f => f.name === 'id')) {
      fields.unshift({ id: crypto.randomUUID(), name: 'id', label: 'ID', fieldType: 16, field_type: 16, required: true, unique: true, system: true });
    }
    const ent = { id, ...body, fields, relations: [], system: false, created_on: new Date().toISOString() };
    entities.set(id, ent);
    records.set(ent.name, new Map());
    return jsonResponse(res, 200, apiEnvelope(ent));
  }
  const entityMatch = path.match(/^\/(?:meta\/entity|entities)\/([^/]+)$/);
  if (entityMatch) {
    const eid = entityMatch[1];
    if (method === 'GET') {
      const e = entities.get(eid) || [...entities.values()].find(x => x.name === eid);
      return e ? jsonResponse(res, 200, apiEnvelope(e)) : jsonResponse(res, 404, apiEnvelope(null, false, 'Not found', 404));
    }
    if (method === 'PUT' || method === 'PATCH') {
      let existing = entities.get(eid) || [...entities.values()].find(x => x.name === eid);
      if (existing) { Object.assign(existing, body); entities.set(existing.id, existing); }
      return jsonResponse(res, 200, apiEnvelope(existing || body));
    }
    if (method === 'DELETE') {
      const e = entities.get(eid) || [...entities.values()].find(x => x.name === eid);
      if (e) { entities.delete(e.id); records.delete(e.name); }
      return jsonResponse(res, 200, apiEnvelope(null, true, 'Deleted'));
    }
  }

  // --- FIELDS ---
  const fieldListMatch = path.match(/^\/(?:meta\/entity|entities)\/([^/]+)\/fields$/);
  if (fieldListMatch) {
    const eid = fieldListMatch[1];
    const ent = entities.get(eid) || [...entities.values()].find(x => x.name === eid);
    if (method === 'GET') {
      return jsonResponse(res, 200, apiEnvelope(ent?.fields || []));
    }
    if (method === 'POST') {
      const fieldId = body.id || crypto.randomUUID();
      const field = { id: fieldId, ...body };
      if (ent) { ent.fields = ent.fields || []; ent.fields.push(field); }
      return jsonResponse(res, 200, apiEnvelope(field));
    }
  }
  const fieldMatch = path.match(/^\/(?:meta\/entity|entities)\/([^/]+)\/fields\/([^/]+)$/);
  if (fieldMatch) {
    const [, eid, fid] = fieldMatch;
    const ent = entities.get(eid) || [...entities.values()].find(x => x.name === eid);
    if (method === 'GET') {
      const f = ent?.fields?.find(x => x.id === fid || x.name === fid);
      return f ? jsonResponse(res, 200, apiEnvelope(f)) : jsonResponse(res, 404, apiEnvelope(null, false, 'Not found', 404));
    }
    if (method === 'PUT' || method === 'PATCH') {
      if (ent) {
        const idx = ent.fields?.findIndex(x => x.id === fid || x.name === fid);
        if (idx >= 0) Object.assign(ent.fields[idx], body);
      }
      return jsonResponse(res, 200, apiEnvelope(body));
    }
    if (method === 'DELETE') {
      if (ent) { ent.fields = (ent.fields || []).filter(x => x.id !== fid && x.name !== fid); }
      return jsonResponse(res, 200, apiEnvelope(null, true, 'Deleted'));
    }
  }

  // --- RELATIONS ---
  if ((path === '/meta/relation' || path === '/relations') && method === 'GET') {
    const allRelations = [...entities.values()].flatMap(e => e.relations || []);
    return jsonResponse(res, 200, apiEnvelope(allRelations));
  }
  if ((path === '/meta/relation' || path === '/relations') && method === 'POST') {
    const id = body.id || crypto.randomUUID();
    const rel = { id, ...body, created_on: new Date().toISOString() };
    // Add to origin entity
    const originEntityId = body.originEntityId || body.origin_entity_id;
    if (originEntityId) {
      const origin = entities.get(originEntityId);
      if (origin) { origin.relations = origin.relations || []; origin.relations.push(rel); }
    }
    // Add to target entity
    const targetEntityId = body.targetEntityId || body.target_entity_id;
    if (targetEntityId) {
      const tgt = entities.get(targetEntityId);
      if (tgt) { tgt.relations = tgt.relations || []; tgt.relations.push(rel); }
    }
    return jsonResponse(res, 200, apiEnvelope(rel));
  }

  // Individual relation by ID
  const relationIdMatch = path.match(/^\/(?:meta\/relation|relations)\/([^/]+)$/);
  if (relationIdMatch) {
    const rid = relationIdMatch[1];
    const allRelations = [...entities.values()].flatMap(e => e.relations || []);
    if (method === 'GET') {
      const r = allRelations.find(x => x.id === rid);
      return r ? jsonResponse(res, 200, apiEnvelope(r)) : jsonResponse(res, 404, apiEnvelope(null, false, 'Not found', 404));
    }
    if (method === 'DELETE') {
      for (const ent of entities.values()) {
        if (ent.relations) ent.relations = ent.relations.filter(x => x.id !== rid);
      }
      return jsonResponse(res, 200, apiEnvelope(null, true, 'Deleted'));
    }
  }

  // --- RECORDS ---
  const recordEntityMatch = path.match(/^\/record\/([^/]+)$/);
  if (recordEntityMatch) {
    let eName = recordEntityMatch[1];
    // Resolve entity UUID to name for record lookups
    if (!records.has(eName)) {
      const ent = entities.get(eName) || [...entities.values()].find(x => x.id === eName);
      if (ent) eName = ent.name;
    }
    if (!records.has(eName)) records.set(eName, new Map());
    const store = records.get(eName);
    if (method === 'GET') {
      const allRecords = [...store.values()];
      return jsonResponse(res, 200, apiEnvelope({ fieldsMeta: [], data: allRecords }));
    }
    if (method === 'POST') {
      const id = body.id || crypto.randomUUID();
      const rec = { id, ...body, created_on: new Date().toISOString() };
      store.set(id, rec);
      return jsonResponse(res, 200, apiEnvelope(rec));
    }
  }
  // Record count endpoint: /record/{entityName}/count
  const recordCountMatch = path.match(/^\/record\/([^/]+)\/count$/);
  if (recordCountMatch && method === 'GET') {
    let eName = recordCountMatch[1];
    if (!records.has(eName)) {
      const ent = entities.get(eName) || [...entities.values()].find(x => x.id === eName);
      if (ent) eName = ent.name;
    }
    const store = records.get(eName) || new Map();
    return jsonResponse(res, 200, apiEnvelope(store.size));
  }

  const recordIdMatch = path.match(/^\/record\/([^/]+)\/([^/]+)$/);
  if (recordIdMatch) {
    let [, eName, rId] = recordIdMatch;
    // Resolve entity UUID to name for record lookups
    if (!records.has(eName)) {
      const ent = entities.get(eName) || [...entities.values()].find(x => x.id === eName);
      if (ent) eName = ent.name;
    }
    const store = records.get(eName) || new Map();
    if (method === 'GET') {
      const r = store.get(rId);
      return r ? jsonResponse(res, 200, apiEnvelope(r)) : jsonResponse(res, 404, apiEnvelope(null, false, 'Not found', 404));
    }
    if (method === 'PUT' || method === 'PATCH') {
      const existing = store.get(rId);
      if (existing) { Object.assign(existing, body); store.set(rId, existing); }
      return jsonResponse(res, 200, apiEnvelope(existing || { id: rId, ...body }));
    }
    if (method === 'DELETE') {
      store.delete(rId);
      return jsonResponse(res, 200, apiEnvelope(null, true, 'Deleted'));
    }
  }

  // --- ACCOUNTS (CRM) ---
  if (path.startsWith('/crm/accounts') || path.startsWith('/accounts')) {
    const accIdMatch = path.match(/\/accounts\/([^/]+)/);
    if (accIdMatch) {
      const aid = accIdMatch[1];
      if (method === 'GET') {
        const a = accounts.get(aid);
        return a ? jsonResponse(res, 200, apiEnvelope(a)) : jsonResponse(res, 404, apiEnvelope(null, false, 'Not found', 404));
      }
      if (method === 'PUT' || method === 'PATCH') {
        const existing = accounts.get(aid);
        if (existing) Object.assign(existing, body);
        return jsonResponse(res, 200, apiEnvelope(existing || body));
      }
      if (method === 'DELETE') {
        accounts.delete(aid);
        return jsonResponse(res, 200, apiEnvelope(null, true, 'Deleted'));
      }
    }
    if (method === 'GET') {
      const allAccounts = [...accounts.values()];
      return jsonResponse(res, 200, apiEnvelope({ records: allAccounts, totalCount: allAccounts.length }));
    }
    if (method === 'POST') {
      const id = body.id || crypto.randomUUID();
      const acc = { id, ...body, created_on: new Date().toISOString() };
      accounts.set(id, acc);
      return jsonResponse(res, 200, apiEnvelope(acc));
    }
  }

  // --- CONTACTS (CRM) ---
  if (path.startsWith('/crm/contacts') || path.startsWith('/contacts')) {
    const cIdMatch = path.match(/\/contacts\/([^/]+)/);
    if (cIdMatch) {
      const cid = cIdMatch[1];
      if (method === 'GET') {
        const c = contacts.get(cid);
        return c ? jsonResponse(res, 200, apiEnvelope(c)) : jsonResponse(res, 404, apiEnvelope(null, false, 'Not found', 404));
      }
      if (method === 'PUT' || method === 'PATCH') {
        const existing = contacts.get(cid);
        if (existing) Object.assign(existing, body);
        return jsonResponse(res, 200, apiEnvelope(existing || body));
      }
      if (method === 'DELETE') {
        contacts.delete(cid);
        return jsonResponse(res, 200, apiEnvelope(null, true, 'Deleted'));
      }
    }
    if (method === 'GET') {
      const allContacts = [...contacts.values()];
      return jsonResponse(res, 200, apiEnvelope({ records: allContacts, totalCount: allContacts.length }));
    }
    if (method === 'POST') {
      const id = body.id || crypto.randomUUID();
      const ct = { id, ...body, created_on: new Date().toISOString() };
      contacts.set(id, ct);
      return jsonResponse(res, 200, apiEnvelope(ct));
    }
  }

  // --- PROJECTS ---
  if (path.startsWith('/projects') || path.startsWith('/inventory/projects')) {
    const pIdMatch = path.match(/\/projects\/([^/]+)/);
    if (pIdMatch) {
      const pid = pIdMatch[1];
      // Tasks under project
      if (path.includes('/tasks')) {
        const taskIdMatch = path.match(/\/tasks\/([^/]+)/);
        if (taskIdMatch) {
          const tid = taskIdMatch[1];
          if (method === 'GET') {
            const t = tasks.get(tid);
            return t ? jsonResponse(res, 200, apiEnvelope(t)) : jsonResponse(res, 404, apiEnvelope(null, false, 'Not found', 404));
          }
          if (method === 'PUT' || method === 'PATCH') {
            const existing = tasks.get(tid);
            if (existing) Object.assign(existing, body);
            return jsonResponse(res, 200, apiEnvelope(existing || body));
          }
          if (method === 'DELETE') {
            tasks.delete(tid);
            return jsonResponse(res, 200, apiEnvelope(null, true, 'Deleted'));
          }
        }
        if (method === 'GET') {
          return jsonResponse(res, 200, apiEnvelope([...tasks.values()].filter(t => t.project_id === pid)));
        }
        if (method === 'POST') {
          const id = body.id || crypto.randomUUID();
          const task = { ...body, id, project_id: pid, created_on: new Date().toISOString() };
          tasks.set(id, task);
          return jsonResponse(res, 200, apiEnvelope(task));
        }
      }
      // Timelogs under project
      if (path.includes('/timelogs')) {
        const tlIdMatch = path.match(/\/timelogs\/([^/]+)/);
        if (tlIdMatch) {
          const tlid = tlIdMatch[1];
          if (method === 'GET') return jsonResponse(res, 200, apiEnvelope(timelogs.get(tlid) || {}));
          if (method === 'DELETE') { timelogs.delete(tlid); return jsonResponse(res, 200, apiEnvelope(null, true, 'Deleted')); }
        }
        if (method === 'GET') return jsonResponse(res, 200, apiEnvelope([...timelogs.values()]));
        if (method === 'POST') {
          const id = crypto.randomUUID();
          const tl = { id, project_id: pid, ...body, created_on: new Date().toISOString() };
          timelogs.set(id, tl);
          return jsonResponse(res, 200, apiEnvelope(tl));
        }
      }
      // Comments
      if (path.includes('/comments')) {
        if (method === 'GET') return jsonResponse(res, 200, apiEnvelope([...comments.values()]));
        if (method === 'POST') {
          const id = crypto.randomUUID();
          const cm = { id, project_id: pid, ...body, author: TEST_USER.email, created_on: new Date().toISOString() };
          comments.set(id, cm);
          return jsonResponse(res, 200, apiEnvelope(cm));
        }
      }
      if (method === 'GET') {
        const p = projects.get(pid);
        return p ? jsonResponse(res, 200, apiEnvelope(p)) : jsonResponse(res, 404, apiEnvelope(null, false, 'Not found', 404));
      }
    }
    if (method === 'GET') {
      return jsonResponse(res, 200, apiEnvelope([...projects.values()]));
    }
  }

  // --- TASKS (standalone) ---
  if (path.startsWith('/tasks') || path.startsWith('/inventory/tasks')) {
    const tIdMatch = path.match(/\/tasks\/([^/]+)/);
    if (tIdMatch) {
      const tid = tIdMatch[1];
      if (path.includes('/timelogs')) {
        if (method === 'GET') return jsonResponse(res, 200, apiEnvelope([...timelogs.values()].filter(tl => tl.task_id === tid)));
        if (method === 'POST') {
          const id = crypto.randomUUID();
          const tl = { id, task_id: tid, ...body, created_on: new Date().toISOString() };
          timelogs.set(id, tl);
          return jsonResponse(res, 200, apiEnvelope(tl));
        }
      }
      if (path.includes('/comments')) {
        if (method === 'GET') return jsonResponse(res, 200, apiEnvelope([...comments.values()].filter(c => c.task_id === tid)));
        if (method === 'POST') {
          const id = crypto.randomUUID();
          const cm = { id, task_id: tid, ...body, author: TEST_USER.email, created_on: new Date().toISOString() };
          comments.set(id, cm);
          return jsonResponse(res, 200, apiEnvelope(cm));
        }
      }
      if (method === 'GET') {
        const t = tasks.get(tid);
        return t ? jsonResponse(res, 200, apiEnvelope(t)) : jsonResponse(res, 404, apiEnvelope(null, false, 'Not found', 404));
      }
      if (method === 'PUT' || method === 'PATCH') {
        const existing = tasks.get(tid);
        if (existing) Object.assign(existing, body);
        return jsonResponse(res, 200, apiEnvelope(existing || body));
      }
      if (method === 'DELETE') {
        tasks.delete(tid);
        return jsonResponse(res, 200, apiEnvelope(null, true, 'Deleted'));
      }
    }
    if (method === 'GET') return jsonResponse(res, 200, apiEnvelope([...tasks.values()]));
    if (method === 'POST') {
      const id = body.id || crypto.randomUUID();
      const task = { ...body, id, created_on: new Date().toISOString() };
      tasks.set(id, task);
      return jsonResponse(res, 200, apiEnvelope(task));
    }
  }

  // --- TIMELOGS (standalone) ---
  if (path.startsWith('/timelogs') || path.startsWith('/inventory/timelogs')) {
    if (method === 'GET') return jsonResponse(res, 200, apiEnvelope([...timelogs.values()]));
    if (method === 'POST') {
      const id = crypto.randomUUID();
      const tl = { id, ...body, created_on: new Date().toISOString() };
      timelogs.set(id, tl);
      return jsonResponse(res, 200, apiEnvelope(tl));
    }
  }

  // --- FILES ---
  if (path.startsWith('/files')) {
    // POST /files/upload-url — step 1: get presigned URL
    if (path === '/files/upload-url' && method === 'POST') {
      const id = crypto.randomUUID();
      return jsonResponse(res, 200, apiEnvelope({
        uploadUrl: `http://localhost:${PORT}/s3-upload/${id}`,
        fileId: id,
      }));
    }
    // POST /files/confirm — step 3: confirm upload and store metadata
    if (path === '/files/confirm' && method === 'POST') {
      const id = body?.fileId || body?.id || crypto.randomUUID();
      const f = {
        id,
        name: body?.filename || body?.name || `file-${id}.txt`,
        filename: body?.filename || body?.name || `file-${id}.txt`,
        content_type: body?.contentType || body?.content_type || 'text/plain',
        contentType: body?.contentType || body?.content_type || 'text/plain',
        size: body?.size || 100,
        width: body?.width || null,
        height: body?.height || null,
        alt: body?.alt || '',
        caption: body?.caption || '',
        path: body?.path || '/',
        created_on: new Date().toISOString(),
        created_by: TEST_USER.sub,
        url: `/s3-proxy/files/${id}`,
      };
      files.set(id, f);
      return jsonResponse(res, 200, apiEnvelope(f));
    }
    // POST /files/upload — direct upload (alternative flow)
    if (path === '/files/upload' && method === 'POST') {
      const id = crypto.randomUUID();
      const fileName = body?.name || body?.fileName || body?.filename || `file-${id}.txt`;
      const f = { id, name: fileName, filename: fileName, content_type: body?.content_type || body?.contentType || 'text/plain', contentType: body?.content_type || body?.contentType || 'text/plain', size: body?.size || 100, created_on: new Date().toISOString(), created_by: TEST_USER.sub, url: `/s3-proxy/files/${id}`, path: '/' };
      files.set(id, f);
      return jsonResponse(res, 200, apiEnvelope(f));
    }
    // User-files endpoints
    if (path.includes('/user-files')) {
      if (method === 'POST') {
        const id = crypto.randomUUID();
        const f = { id, name: body?.name || `user-file-${id}.txt`, filename: body?.name || `user-file-${id}.txt`, created_on: new Date().toISOString(), created_by: TEST_USER.sub, path: '/' };
        files.set(id, f);
        return jsonResponse(res, 200, apiEnvelope(f));
      }
      if (method === 'GET') {
        const allFiles = [...files.values()];
        return jsonResponse(res, 200, apiEnvelope({ files: allFiles, totalCount: allFiles.length, page: 1, pageSize: 50 }));
      }
    }
    // GET/PUT/DELETE /files/{id}/* — individual file operations
    const fileIdMatch = path.match(/\/files\/([0-9a-f-]{36})(\/.*)?$/);
    if (fileIdMatch) {
      const fid = fileIdMatch[1];
      const subPath = fileIdMatch[2] || '';
      // GET /files/{id}/download-url
      if (subPath === '/download-url' && method === 'GET') {
        const f = files.get(fid);
        if (!f) return jsonResponse(res, 404, apiEnvelope(null, false, 'Not found', 404));
        return jsonResponse(res, 200, apiEnvelope({ downloadUrl: `http://localhost:${PORT}/s3-proxy/files/${fid}`, expiresIn: 3600 }));
      }
      if (method === 'GET') {
        const f = files.get(fid);
        return f ? jsonResponse(res, 200, apiEnvelope(f)) : jsonResponse(res, 404, apiEnvelope(null, false, 'Not found', 404));
      }
      if (method === 'PUT' || method === 'PATCH') {
        const existing = files.get(fid);
        if (existing) Object.assign(existing, body);
        return jsonResponse(res, 200, apiEnvelope(existing || body));
      }
      if (method === 'DELETE') {
        files.delete(fid);
        return jsonResponse(res, 200, apiEnvelope(null, true, 'Deleted'));
      }
    }
    // GET /files or /files/list — file listing
    if ((path === '/files' || path === '/files/list') && method === 'GET') {
      const allFiles = [...files.values()];
      return jsonResponse(res, 200, apiEnvelope({ files: allFiles, totalCount: allFiles.length, page: 1, pageSize: 50 }));
    }
  }

  // --- S3 Upload Mock (presigned URL target) ---
  if (path.startsWith('/s3-upload/') && method === 'PUT') {
    return jsonResponse(res, 200, '');
  }
  // --- S3 Download Mock ---
  if (path.startsWith('/s3-proxy/files/') && method === 'GET') {
    res.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Content-Length': '12', 'Content-Disposition': 'attachment; filename="test.txt"' });
    return res.end('test content');
  }

  // --- NOTIFICATIONS / EMAILS ---
  if (path.startsWith('/notifications') || path.startsWith('/emails')) {
    // Templates
    if (path.includes('/templates')) {
      const tplIdMatch = path.match(/\/templates\/([^/]+)/);
      if (tplIdMatch) {
        const tid = tplIdMatch[1];
        if (method === 'GET') return jsonResponse(res, 200, apiEnvelope({ id: tid, name: 'Test Template', subject: 'Test Subject', body: '<p>Hello {{name}}</p>', created_on: new Date().toISOString() }));
        if (method === 'PUT') return jsonResponse(res, 200, apiEnvelope({ id: tid, ...body }));
        if (method === 'DELETE') return jsonResponse(res, 200, apiEnvelope(null, true, 'Deleted'));
      }
      if (method === 'GET') return jsonResponse(res, 200, apiEnvelope({ templates: [{ id: crypto.randomUUID(), name: 'Default Template', subject: 'Welcome', body: '<p>Welcome</p>', created_on: new Date().toISOString() }], totalCount: 1, page: 1, pageSize: 50 }));
      if (method === 'POST') { const id = crypto.randomUUID(); return jsonResponse(res, 200, apiEnvelope({ id, ...body, created_on: new Date().toISOString() })); }
    }
    // SMTP Configs
    if (path.includes('/smtp-configs') || path.includes('/smtp_service')) {
      const cfgIdMatch = path.match(/\/smtp-configs?\/([^/]+)/);
      if (cfgIdMatch) {
        const cid = cfgIdMatch[1];
        if (method === 'GET') return jsonResponse(res, 200, apiEnvelope({ id: cid, name: 'Test SMTP', host: 'smtp.test.local', port: 587, created_on: new Date().toISOString() }));
        if (method === 'PUT') return jsonResponse(res, 200, apiEnvelope({ id: cid, ...body }));
        if (method === 'DELETE') return jsonResponse(res, 200, apiEnvelope(null, true, 'Deleted'));
      }
      if (method === 'GET') return jsonResponse(res, 200, apiEnvelope({ configs: [{ id: crypto.randomUUID(), name: 'Default SMTP', host: 'smtp.test.local', port: 587, enabled: true }], totalCount: 1, page: 1, pageSize: 50 }));
      if (method === 'POST') { const id = crypto.randomUUID(); return jsonResponse(res, 200, apiEnvelope({ id, ...body, created_on: new Date().toISOString() })); }
    }
    // Send / Queue
    if (path.includes('/emails/send') || path.includes('/email/send')) {
      const id = crypto.randomUUID();
      const em = { id, ...body, status: 'sent', type: 'email', sent_on: new Date().toISOString(), created_on: new Date().toISOString() };
      emails.set(id, em);
      return jsonResponse(res, 200, apiEnvelope(em));
    }
    if (path.includes('/emails/queue') || path.includes('/email/queue')) {
      const id = crypto.randomUUID();
      const em = { id, ...body, status: 'pending', type: 'email', created_on: new Date().toISOString() };
      emails.set(id, em);
      return jsonResponse(res, 200, apiEnvelope(em));
    }
    // Individual notification by ID
    const emIdMatch = path.match(/\/(?:emails?|notifications)\/([0-9a-f-]{36})/);
    if (emIdMatch) {
      const eid = emIdMatch[1];
      if (method === 'GET') {
        const e = emails.get(eid);
        return e ? jsonResponse(res, 200, apiEnvelope(e)) : jsonResponse(res, 404, apiEnvelope(null, false, 'Not found', 404));
      }
      if (method === 'PUT' || method === 'PATCH') {
        const existing = emails.get(eid);
        if (existing) Object.assign(existing, body);
        return jsonResponse(res, 200, apiEnvelope(existing || body));
      }
      if (method === 'DELETE') {
        emails.delete(eid);
        return jsonResponse(res, 200, apiEnvelope(null, true, 'Deleted'));
      }
    }
    // Email list — GET /notifications/emails (EmailList page uses { data, total, page, pageSize })
    if (method === 'GET' && (path === '/notifications/emails' || path === '/emails')) {
      const reqUrl = new URL(req.url, `http://localhost:${PORT}`);
      const status = reqUrl.searchParams.get('status');
      let filtered = [...emails.values()];
      if (status) filtered = filtered.filter(e => e.status === status);
      const pg = parseInt(reqUrl.searchParams.get('page') || '1', 10);
      const ps = parseInt(reqUrl.searchParams.get('pageSize') || '50', 10);
      return jsonResponse(res, 200, apiEnvelope({ data: filtered, total: filtered.length, page: pg, pageSize: ps }));
    }
    // Notification list — GET /notifications (useNotifications hook uses { notifications, totalCount, page, pageSize })
    if (method === 'GET') {
      const allNotifications = [...emails.values()];
      return jsonResponse(res, 200, apiEnvelope({ notifications: allNotifications, totalCount: allNotifications.length, page: 1, pageSize: 50 }));
    }
    if (method === 'POST') {
      const id = crypto.randomUUID();
      const em = { id, ...body, status: 'draft', type: body?.type || 'email', created_on: new Date().toISOString() };
      emails.set(id, em);
      return jsonResponse(res, 200, apiEnvelope(em));
    }
  }

  // --- PLUGINS ---
  if (path.startsWith('/plugins')) {
    const plIdMatch = path.match(/\/plugins\/([^/]+)/);
    if (plIdMatch) {
      const pid = plIdMatch[1];
      if (method === 'GET') {
        const p = plugins.get(pid);
        return p ? jsonResponse(res, 200, apiEnvelope(p)) : jsonResponse(res, 404, apiEnvelope(null, false, 'Not found', 404));
      }
    }
    if (method === 'GET') return jsonResponse(res, 200, apiEnvelope([...plugins.values()]));
  }

  // --- WORKFLOWS ---
  if (path.startsWith('/workflows')) {
    if (method === 'GET') return jsonResponse(res, 200, apiEnvelope([...workflows.values()]));
    if (method === 'POST') {
      const id = crypto.randomUUID();
      const wf = { id, ...body, status: 'running', created_on: new Date().toISOString() };
      workflows.set(id, wf);
      return jsonResponse(res, 200, apiEnvelope(wf));
    }
  }

  // --- REPORTS ---
  if (path.startsWith('/reports')) {
    if (method === 'GET') return jsonResponse(res, 200, apiEnvelope([...reports.values()]));
    if (method === 'POST') {
      const id = crypto.randomUUID();
      const rp = { id, ...body, created_on: new Date().toISOString() };
      reports.set(id, rp);
      return jsonResponse(res, 200, apiEnvelope(rp));
    }
  }

  // --- DATASOURCE ---
  if (path.startsWith('/datasource') || path.startsWith('/eql')) {
    return jsonResponse(res, 200, apiEnvelope([]));
  }

  // --- SEARCH ---
  if (path.startsWith('/search')) {
    return jsonResponse(res, 200, apiEnvelope([]));
  }

  // --- APPS (sitemap) ---
  if (path.startsWith('/apps') || path === '/sitemap') {
    return jsonResponse(res, 200, apiEnvelope(global.__apps || []));
  }

  // --- PAGES ---
  if (path.startsWith('/pages')) {
    return jsonResponse(res, 200, apiEnvelope([]));
  }

  // --- INVENTORY / DASHBOARD ---
  if (path.startsWith('/inventory')) {
    if (path.includes('/dashboard')) return jsonResponse(res, 200, apiEnvelope({ widgets: [], stats: {}, recent_activity: [] }));
    return jsonResponse(res, 200, apiEnvelope([]));
  }

  // --- CRM lookup tables ---
  if (path.startsWith('/crm/salutations') || path.startsWith('/salutations')) return jsonResponse(res, 200, apiEnvelope(['Mr.', 'Mrs.', 'Ms.', 'Dr.', 'Prof.']));
  if (path.startsWith('/crm/countries') || path.startsWith('/countries')) return jsonResponse(res, 200, apiEnvelope([{ id: 'US', name: 'United States' }, { id: 'UK', name: 'United Kingdom' }]));
  if (path.startsWith('/crm/languages') || path.startsWith('/languages')) return jsonResponse(res, 200, apiEnvelope([{ id: 'en', name: 'English' }]));
  if (path.startsWith('/crm/currencies') || path.startsWith('/currencies')) return jsonResponse(res, 200, apiEnvelope([{ id: 'USD', name: 'US Dollar', symbol: '$' }]));

  // --- INVOICING ---
  if (path.startsWith('/invoicing')) return jsonResponse(res, 200, apiEnvelope([]));

  // --- SMTP / SERVICES ---
  if (path.startsWith('/smtp') || path.startsWith('/services')) return jsonResponse(res, 200, apiEnvelope([]));

  // --- DEFAULT (catch-all returns success with empty data) ---
  return jsonResponse(res, 200, apiEnvelope(null, true, `Mock: ${method} ${pathname}`));
}

// ---------------------------------------------------------------------------
// Server
// ---------------------------------------------------------------------------

const server = http.createServer(async (req, res) => {
  // CORS preflight
  if (req.method === 'OPTIONS') {
    res.writeHead(204, {
      'Access-Control-Allow-Origin': '*',
      'Access-Control-Allow-Methods': 'GET,POST,PUT,PATCH,DELETE,OPTIONS',
      'Access-Control-Allow-Headers': '*',
      'Access-Control-Max-Age': '86400',
    });
    return res.end();
  }

  const url = new URL(req.url, `http://localhost:${PORT}`);
  const pathname = url.pathname;
  const method = req.method;
  const body = await parseBody(req);

  // Check if this is a Cognito request
  const amzTarget = req.headers['x-amz-target'] || '';
  if (amzTarget.includes('CognitoIdentityProviderService') || pathname === '/' && amzTarget) {
    return handleCognito(req, res, body);
  }

  // API request
  return handleApi(req, res, body, pathname, method);
});

server.listen(PORT, () => {
  console.log(`[E2E Mock Server] Listening on http://localhost:${PORT}`);
  console.log(`[E2E Mock Server] Seeded: ${entities.size} entities, ${accounts.size} accounts, ${contacts.size} contacts, ${tasks.size} tasks, ${emails.size} emails`);
});
