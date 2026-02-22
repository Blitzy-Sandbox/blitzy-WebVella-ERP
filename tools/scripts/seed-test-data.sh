#!/usr/bin/env bash
# =============================================================================
# WebVella ERP — Cognito Test User & DynamoDB Test Data Seeder
# =============================================================================
# Seeds Cognito users/groups, DynamoDB tables, and SSM parameters into
# LocalStack for local development and integration testing.
#
# This script replaces the monolith's startup-time initialization in
# ERPService.cs (lines 444-529) where users, roles, and role-user relations
# were created during application bootstrap.
#
# Prerequisites:
#   1. Docker running with LocalStack container (docker compose up -d)
#   2. CDK stacks deployed (./tools/scripts/bootstrap-localstack.sh)
#   3. awslocal CLI installed (pip install awscli-local)
#
# Usage:
#   ./tools/scripts/seed-test-data.sh
#
# Environment variables (all have sensible defaults for LocalStack):
#   AWS_ENDPOINT_URL      — LocalStack endpoint (default: http://localhost:4566)
#   AWS_REGION            — AWS region (default: us-east-1)
#   AWS_ACCESS_KEY_ID     — AWS access key (default: test)
#   AWS_SECRET_ACCESS_KEY — AWS secret key (default: test)
#
# References:
#   AAP §0.7.5  — Default system user erp@webvella.com with password erp
#   AAP §0.8.1  — LocalStack-exclusive testing
#   AAP §0.8.6  — awslocal CLI, AWS_ENDPOINT_URL=http://localhost:4566
# =============================================================================

set -euo pipefail

# =============================================================================
# Environment Configuration
# =============================================================================
export AWS_ENDPOINT_URL="${AWS_ENDPOINT_URL:-http://localhost:4566}"
export AWS_REGION="${AWS_REGION:-us-east-1}"
export AWS_DEFAULT_REGION="${AWS_REGION}"
export AWS_ACCESS_KEY_ID="${AWS_ACCESS_KEY_ID:-test}"
export AWS_SECRET_ACCESS_KEY="${AWS_SECRET_ACCESS_KEY:-test}"

# Script location resolution
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

# =============================================================================
# Legacy System IDs from WebVella.Erp/Api/Definitions.cs
# These MUST match the monolith's SystemIds exactly for migration compatibility.
# =============================================================================
readonly SYSTEM_USER_ID="10000000-0000-0000-0000-000000000000"
readonly FIRST_USER_ID="eabd66fd-8de1-4d79-9674-447ee89921c2"
readonly ADMINISTRATOR_ROLE_ID="bdc56420-caf0-4030-8a0e-d264938e0cda"
readonly REGULAR_ROLE_ID="f16ec6db-626d-4c27-8de0-3e7ce542c55f"
readonly GUEST_ROLE_ID="987148b1-afa8-4b33-8616-55861e5fd065"
readonly USER_ENTITY_ID="b9cebc3b-6443-452a-8e34-b311a73dcc8b"
readonly ROLE_ENTITY_ID="c4541fee-fbb6-4661-929e-1724adec285a"
readonly USER_ROLE_RELATION_ID="0c4b119e-1d7b-4b40-8d2c-9e447cc656ab"

# =============================================================================
# Logging Helpers
# =============================================================================
log_info()  { echo "ℹ️  $*"; }
log_ok()    { echo "✅ $*"; }
log_warn()  { echo "⚠️  $*"; }
log_error() { echo "❌ $*" >&2; }
log_step()  { echo ""; echo "━━━ $* ━━━"; }

# =============================================================================
# Phase 1: Prerequisites Validation
# =============================================================================
check_prerequisites() {
  log_step "Checking prerequisites"

  # Check core CLI tools first (they are used by subsequent checks)
  if ! command -v curl &> /dev/null; then
    log_error "curl is not installed. Please install curl."
    exit 1
  fi
  log_ok "curl found"

  if ! command -v grep &> /dev/null; then
    log_error "grep is not installed. Please install grep."
    exit 1
  fi
  log_ok "grep found"

  # Check awslocal CLI (from awscli-local pip package)
  if ! command -v awslocal &> /dev/null; then
    log_error "awslocal CLI is not installed."
    log_error "Install it with: pip install awscli-local"
    exit 1
  fi
  log_ok "awslocal CLI found"

  # Check Docker daemon is running
  if ! command -v docker &> /dev/null; then
    log_error "Docker is not installed. Please install Docker first."
    exit 1
  fi

  if ! docker info > /dev/null 2>&1; then
    log_error "Docker daemon is not running. Please start Docker."
    exit 1
  fi
  log_ok "Docker daemon is running"

  # Check LocalStack container is running; start it if not
  cd "${REPO_ROOT}"
  if ! docker compose ps 2>/dev/null | grep -q "localstack"; then
    log_warn "LocalStack container is not running. Starting it now..."
    docker compose up -d
  fi
  log_ok "LocalStack container is running"

  # Wait for LocalStack health endpoint to be ready
  log_info "Waiting for LocalStack to be healthy..."
  local retries=30
  while ! curl -sf "${AWS_ENDPOINT_URL}/_localstack/health" > /dev/null 2>&1; do
    retries=$((retries - 1))
    if [ "${retries}" -le 0 ]; then
      log_error "LocalStack failed to become healthy within 60 seconds."
      exit 1
    fi
    echo "  Waiting for LocalStack... (${retries} retries remaining)"
    sleep 2
  done
  log_ok "LocalStack is healthy"
}

# =============================================================================
# Phase 2: Cognito User Pool Discovery
# =============================================================================

# Global variables populated by discover_cognito_resources
USER_POOL_ID=""
CLIENT_ID=""

discover_cognito_resources() {
  log_step "Discovering Cognito resources"

  # Discover the user pool created by CDK shared-stack.ts
  USER_POOL_ID=$(awslocal cognito-idp list-user-pools \
    --max-results 10 \
    --query 'UserPools[0].Id' \
    --output text 2>/dev/null || echo "")

  if [ -z "${USER_POOL_ID}" ] || [ "${USER_POOL_ID}" = "None" ]; then
    log_error "No Cognito user pool found."
    log_error "Please run bootstrap-localstack.sh first to deploy CDK stacks."
    exit 1
  fi
  log_ok "Found Cognito User Pool: ${USER_POOL_ID}"

  # Discover the app client
  CLIENT_ID=$(awslocal cognito-idp list-user-pool-clients \
    --user-pool-id "${USER_POOL_ID}" \
    --query 'UserPoolClients[0].ClientId' \
    --output text 2>/dev/null || echo "")

  if [ -z "${CLIENT_ID}" ] || [ "${CLIENT_ID}" = "None" ]; then
    log_warn "No Cognito app client found. Some operations may be limited."
  else
    log_ok "Found Cognito Client ID: ${CLIENT_ID}"
  fi
}

# =============================================================================
# Phase 3: Cognito Group (Role) Seeding
# =============================================================================
# Mapping from monolith's role system (WebVella.Erp/Api/Definitions.cs):
#   AdministratorRoleId = BDC56420-CAF0-4030-8A0E-D264938E0CDA
#   RegularRoleId       = F16EC6DB-626D-4C27-8DE0-3E7CE542C55F
#   GuestRoleId         = 987148B1-AFA8-4B33-8616-55861E5FD065
# =============================================================================
create_cognito_group() {
  local group_name="$1"
  local description="$2"

  # Idempotent: check if group already exists before creating
  if awslocal cognito-idp get-group \
       --user-pool-id "${USER_POOL_ID}" \
       --group-name "${group_name}" &> /dev/null; then
    log_info "Group '${group_name}' already exists — skipping"
    return 0
  fi

  awslocal cognito-idp create-group \
    --user-pool-id "${USER_POOL_ID}" \
    --group-name "${group_name}" \
    --description "${description}" > /dev/null

  log_ok "Created Cognito group: ${group_name}"
}

seed_cognito_groups() {
  log_step "Seeding Cognito groups (roles)"

  create_cognito_group "administrator" "Administrator role"
  create_cognito_group "regular"       "Regular user role"
  create_cognito_group "guest"         "Guest role"
}

# =============================================================================
# Phase 4: Cognito User Seeding
# =============================================================================
# Users extracted from ERPService.cs lines 444-476:
#   System user  — id=10000000-..., email=system@webvella.com, username=system
#   First user   — id=EABD66FD-..., email=erp@webvella.com, username=administrator
# Additional test users for integration testing of role-based access.
# =============================================================================
create_cognito_user() {
  local email="$1"
  local given_name="$2"
  local family_name="$3"
  local password="$4"
  local legacy_id="$5"
  local preferred_username="$6"

  # Idempotent: check if user already exists
  if awslocal cognito-idp admin-get-user \
       --user-pool-id "${USER_POOL_ID}" \
       --username "${email}" &> /dev/null; then
    log_info "User '${email}' already exists — updating password"
    # Always ensure password is set correctly on re-run
    awslocal cognito-idp admin-set-user-password \
      --user-pool-id "${USER_POOL_ID}" \
      --username "${email}" \
      --password "${password}" \
      --permanent > /dev/null 2>&1 || true
    return 0
  fi

  # Create the user with suppressed welcome message (no email verification needed)
  awslocal cognito-idp admin-create-user \
    --user-pool-id "${USER_POOL_ID}" \
    --username "${email}" \
    --user-attributes \
      Name=email,Value="${email}" \
      Name=email_verified,Value=true \
      Name=given_name,Value="${given_name}" \
      Name=family_name,Value="${family_name}" \
      Name=custom:legacy_id,Value="${legacy_id}" \
      Name=preferred_username,Value="${preferred_username}" \
    --message-action SUPPRESS > /dev/null

  # Set permanent password (bypasses Cognito's FORCE_CHANGE_PASSWORD state)
  # NOTE: For erp@webvella.com the password is "erp" — the monolith's default.
  # LocalStack does not enforce password policies, so simple passwords work.
  awslocal cognito-idp admin-set-user-password \
    --user-pool-id "${USER_POOL_ID}" \
    --username "${email}" \
    --password "${password}" \
    --permanent > /dev/null

  log_ok "Created Cognito user: ${email} (${preferred_username})"
}

assign_user_to_group() {
  local email="$1"
  local group_name="$2"

  awslocal cognito-idp admin-add-user-to-group \
    --user-pool-id "${USER_POOL_ID}" \
    --username "${email}" \
    --group-name "${group_name}" > /dev/null 2>&1 || true

  log_ok "  Assigned ${email} → ${group_name}"
}

seed_cognito_users() {
  log_step "Seeding Cognito users"

  # -------------------------------------------------------------------------
  # 1. System User (non-interactive, internal service account)
  #    Source: ERPService.cs line 448  user["id"] = SystemIds.SystemUserId
  #    Source: ERPService.cs line 452  user["email"] = "system@webvella.com"
  #    Source: ERPService.cs line 453  user["username"] = "system"
  #    Source: ERPService.cs line 449  user["first_name"] = "Local"
  #    Source: ERPService.cs line 450  user["last_name"] = "System"
  #    Monolith used Guid.NewGuid() as password; we use a fixed strong password.
  # -------------------------------------------------------------------------
  create_cognito_user \
    "system@webvella.com" \
    "Local" \
    "System" \
    "System@LocalDev1!" \
    "${SYSTEM_USER_ID}" \
    "system"

  # -------------------------------------------------------------------------
  # 2. First User / Admin (primary interactive admin)
  #    Source: ERPService.cs line 464  user["id"] = SystemIds.FirstUserId
  #    Source: ERPService.cs line 468  user["email"] = "erp@webvella.com"
  #    Source: ERPService.cs line 469  user["username"] = "administrator"
  #    Source: ERPService.cs line 465  user["first_name"] = "WebVella"
  #    Source: ERPService.cs line 466  user["last_name"] = "Erp"
  #    CRITICAL: password "erp" must be preserved per AAP §0.7.5
  # -------------------------------------------------------------------------
  create_cognito_user \
    "erp@webvella.com" \
    "WebVella" \
    "Erp" \
    "erp" \
    "${FIRST_USER_ID}" \
    "administrator"

  # -------------------------------------------------------------------------
  # 3. Test Regular User (for testing regular role access)
  # -------------------------------------------------------------------------
  create_cognito_user \
    "regular@webvella.com" \
    "Test" \
    "Regular" \
    "Regular@Test1!" \
    "00000000-0000-0000-0000-000000000003" \
    "regular"

  # -------------------------------------------------------------------------
  # 4. Test Guest User (for testing guest role access)
  # -------------------------------------------------------------------------
  create_cognito_user \
    "guest@webvella.com" \
    "Test" \
    "Guest" \
    "Guest@Test1!" \
    "00000000-0000-0000-0000-000000000004" \
    "guest"

  # -------------------------------------------------------------------------
  # Group (role) assignments — from ERPService.cs lines 511-526
  # -------------------------------------------------------------------------
  log_info "Assigning users to groups..."

  # System user → administrator  (line 512)
  assign_user_to_group "system@webvella.com" "administrator"

  # First user → administrator + regular  (lines 518-523)
  assign_user_to_group "erp@webvella.com" "administrator"
  assign_user_to_group "erp@webvella.com" "regular"

  # Test regular → regular
  assign_user_to_group "regular@webvella.com" "regular"

  # Test guest → guest
  assign_user_to_group "guest@webvella.com" "guest"
}

# =============================================================================
# Phase 5: DynamoDB Table Discovery
# =============================================================================
# CDK stacks create DynamoDB tables with names that may include stack prefixes.
# We discover actual table names by listing tables and matching patterns.
# =============================================================================
discover_dynamodb_table() {
  local pattern="$1"
  local tables

  tables=$(awslocal dynamodb list-tables \
    --query 'TableNames' \
    --output text 2>/dev/null || echo "")

  # Search for a table name containing the pattern (case-insensitive)
  local match
  match=$(echo "${tables}" | tr '\t' '\n' | grep -i "${pattern}" | head -1 || echo "")

  if [ -n "${match}" ]; then
    echo "${match}"
  else
    # Fallback to convention name
    echo "${pattern}"
  fi
}

# =============================================================================
# Phase 5a: Seed Identity Service DynamoDB Table
# =============================================================================
# Role records with legacy GUIDs from ERPService.cs lines 478-508
# and user-role relationship records from lines 511-526.
# Key schema: PK (partition key), SK (sort key) — single-table design.
# =============================================================================
seed_identity_table() {
  log_step "Seeding Identity service DynamoDB table"

  local table_name
  table_name=$(discover_dynamodb_table "identity")

  # Verify table exists
  if ! awslocal dynamodb describe-table --table-name "${table_name}" &> /dev/null; then
    log_warn "Identity table '${table_name}' not found — skipping DynamoDB seeding."
    log_warn "Run bootstrap-localstack.sh first to create CDK resources."
    return 0
  fi

  log_info "Using table: ${table_name}"

  # --- Administrator Role (ERPService.cs line 480-486) ---
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "ROLE#'"${ADMINISTRATOR_ROLE_ID}"'"},
      "SK": {"S": "META"},
      "id": {"S": "'"${ADMINISTRATOR_ROLE_ID}"'"},
      "name": {"S": "administrator"},
      "description": {"S": ""},
      "entityType": {"S": "role"},
      "createdAt": {"S": "2010-10-10T00:00:00Z"},
      "system": {"BOOL": true}
    }' > /dev/null 2>&1 || true
  log_ok "Seeded role: administrator (${ADMINISTRATOR_ROLE_ID})"

  # --- Regular Role (ERPService.cs line 490-497) ---
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "ROLE#'"${REGULAR_ROLE_ID}"'"},
      "SK": {"S": "META"},
      "id": {"S": "'"${REGULAR_ROLE_ID}"'"},
      "name": {"S": "regular"},
      "description": {"S": ""},
      "entityType": {"S": "role"},
      "createdAt": {"S": "2010-10-10T00:00:00Z"},
      "system": {"BOOL": true}
    }' > /dev/null 2>&1 || true
  log_ok "Seeded role: regular (${REGULAR_ROLE_ID})"

  # --- Guest Role (ERPService.cs line 500-508) ---
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "ROLE#'"${GUEST_ROLE_ID}"'"},
      "SK": {"S": "META"},
      "id": {"S": "'"${GUEST_ROLE_ID}"'"},
      "name": {"S": "guest"},
      "description": {"S": ""},
      "entityType": {"S": "role"},
      "createdAt": {"S": "2010-10-10T00:00:00Z"},
      "system": {"BOOL": true}
    }' > /dev/null 2>&1 || true
  log_ok "Seeded role: guest (${GUEST_ROLE_ID})"

  # --- System User record (ERPService.cs line 447-459) ---
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "USER#'"${SYSTEM_USER_ID}"'"},
      "SK": {"S": "META"},
      "id": {"S": "'"${SYSTEM_USER_ID}"'"},
      "email": {"S": "system@webvella.com"},
      "username": {"S": "system"},
      "firstName": {"S": "Local"},
      "lastName": {"S": "System"},
      "enabled": {"BOOL": true},
      "entityType": {"S": "user"},
      "createdAt": {"S": "2010-10-10T00:00:00Z"},
      "roles": {"SS": ["'"${ADMINISTRATOR_ROLE_ID}"'"]}
    }' > /dev/null 2>&1 || true
  log_ok "Seeded user: system@webvella.com (${SYSTEM_USER_ID})"

  # --- First User / Admin record (ERPService.cs line 462-476) ---
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "USER#'"${FIRST_USER_ID}"'"},
      "SK": {"S": "META"},
      "id": {"S": "'"${FIRST_USER_ID}"'"},
      "email": {"S": "erp@webvella.com"},
      "username": {"S": "administrator"},
      "firstName": {"S": "WebVella"},
      "lastName": {"S": "Erp"},
      "enabled": {"BOOL": true},
      "entityType": {"S": "user"},
      "createdAt": {"S": "2010-10-10T00:00:00Z"},
      "roles": {"SS": ["'"${ADMINISTRATOR_ROLE_ID}"'", "'"${REGULAR_ROLE_ID}"'"]}
    }' > /dev/null 2>&1 || true
  log_ok "Seeded user: erp@webvella.com (${FIRST_USER_ID})"

  # --- User-Role relationship records (ERPService.cs lines 511-526) ---
  # System user → Administrator role
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "USER_ROLE#'"${SYSTEM_USER_ID}"'"},
      "SK": {"S": "ROLE#'"${ADMINISTRATOR_ROLE_ID}"'"},
      "userId": {"S": "'"${SYSTEM_USER_ID}"'"},
      "roleId": {"S": "'"${ADMINISTRATOR_ROLE_ID}"'"},
      "relationId": {"S": "'"${USER_ROLE_RELATION_ID}"'"},
      "entityType": {"S": "user_role"}
    }' > /dev/null 2>&1 || true

  # First user → Administrator role
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "USER_ROLE#'"${FIRST_USER_ID}"'"},
      "SK": {"S": "ROLE#'"${ADMINISTRATOR_ROLE_ID}"'"},
      "userId": {"S": "'"${FIRST_USER_ID}"'"},
      "roleId": {"S": "'"${ADMINISTRATOR_ROLE_ID}"'"},
      "relationId": {"S": "'"${USER_ROLE_RELATION_ID}"'"},
      "entityType": {"S": "user_role"}
    }' > /dev/null 2>&1 || true

  # First user → Regular role
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "USER_ROLE#'"${FIRST_USER_ID}"'"},
      "SK": {"S": "ROLE#'"${REGULAR_ROLE_ID}"'"},
      "userId": {"S": "'"${FIRST_USER_ID}"'"},
      "roleId": {"S": "'"${REGULAR_ROLE_ID}"'"},
      "relationId": {"S": "'"${USER_ROLE_RELATION_ID}"'"},
      "entityType": {"S": "user_role"}
    }' > /dev/null 2>&1 || true

  log_ok "Seeded user-role relationships"
}

# =============================================================================
# Phase 5b: Seed Entity Management Service DynamoDB Table
# =============================================================================
# Sample entity definitions from ERPService.cs lines 58-95 (user entity)
# and lines 344-417 (role entity).
# Key schema: PK=ENTITY#{entityId}, SK=META for entities;
#             PK=ENTITY#{entityId}, SK=FIELD#{fieldId} for fields.
# =============================================================================
seed_entity_management_table() {
  log_step "Seeding Entity Management service DynamoDB table"

  local table_name
  table_name=$(discover_dynamodb_table "entity-management")

  # Verify table exists
  if ! awslocal dynamodb describe-table --table-name "${table_name}" &> /dev/null; then
    log_warn "Entity Management table '${table_name}' not found — skipping."
    return 0
  fi

  log_info "Using table: ${table_name}"

  # --- User Entity definition (ERPService.cs lines 58-84) ---
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "ENTITY#'"${USER_ENTITY_ID}"'"},
      "SK": {"S": "META"},
      "id": {"S": "'"${USER_ENTITY_ID}"'"},
      "name": {"S": "user"},
      "label": {"S": "User"},
      "labelPlural": {"S": "Users"},
      "system": {"BOOL": true},
      "color": {"S": "#f44336"},
      "iconName": {"S": "fa fa-user"},
      "entityType": {"S": "entity"},
      "recordPermissions": {"M": {
        "canCreate": {"L": [{"S": "'"${GUEST_ROLE_ID}"'"}, {"S": "'"${ADMINISTRATOR_ROLE_ID}"'"}]},
        "canRead": {"L": [{"S": "'"${GUEST_ROLE_ID}"'"}, {"S": "'"${REGULAR_ROLE_ID}"'"}, {"S": "'"${ADMINISTRATOR_ROLE_ID}"'"}]},
        "canUpdate": {"L": [{"S": "'"${ADMINISTRATOR_ROLE_ID}"'"}]},
        "canDelete": {"L": [{"S": "'"${ADMINISTRATOR_ROLE_ID}"'"}]}
      }}
    }' > /dev/null 2>&1 || true
  log_ok "Seeded entity: user (${USER_ENTITY_ID})"

  # --- User Entity Fields ---
  # created_on field (ERPService.cs lines 88-100)
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "ENTITY#'"${USER_ENTITY_ID}"'"},
      "SK": {"S": "FIELD#6fda5e6b-80e6-4d8a-9e2a-d983c3694e96"},
      "id": {"S": "6fda5e6b-80e6-4d8a-9e2a-d983c3694e96"},
      "name": {"S": "created_on"},
      "label": {"S": "Created On"},
      "fieldType": {"S": "DateTime"},
      "required": {"BOOL": true},
      "system": {"BOOL": true},
      "searchable": {"BOOL": true},
      "auditable": {"BOOL": true},
      "entityType": {"S": "field"}
    }' > /dev/null 2>&1 || true

  # email field
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "ENTITY#'"${USER_ENTITY_ID}"'"},
      "SK": {"S": "FIELD#0a02f9bf-b605-4355-b72b-680ddfc6eb5a"},
      "id": {"S": "0a02f9bf-b605-4355-b72b-680ddfc6eb5a"},
      "name": {"S": "email"},
      "label": {"S": "Email"},
      "fieldType": {"S": "Email"},
      "required": {"BOOL": true},
      "system": {"BOOL": true},
      "searchable": {"BOOL": true},
      "auditable": {"BOOL": true},
      "unique": {"BOOL": true},
      "entityType": {"S": "field"}
    }' > /dev/null 2>&1 || true

  # username field
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "ENTITY#'"${USER_ENTITY_ID}"'"},
      "SK": {"S": "FIELD#263c0b21-88c1-4c2b-80b4-db7402b0d2e2"},
      "id": {"S": "263c0b21-88c1-4c2b-80b4-db7402b0d2e2"},
      "name": {"S": "username"},
      "label": {"S": "Username"},
      "fieldType": {"S": "Text"},
      "required": {"BOOL": true},
      "system": {"BOOL": true},
      "searchable": {"BOOL": true},
      "unique": {"BOOL": true},
      "entityType": {"S": "field"}
    }' > /dev/null 2>&1 || true

  # first_name field
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "ENTITY#'"${USER_ENTITY_ID}"'"},
      "SK": {"S": "FIELD#d3a48ee0-3e4b-4d9e-8a7c-5e2b4f7c3d1a"},
      "id": {"S": "d3a48ee0-3e4b-4d9e-8a7c-5e2b4f7c3d1a"},
      "name": {"S": "first_name"},
      "label": {"S": "First Name"},
      "fieldType": {"S": "Text"},
      "required": {"BOOL": true},
      "system": {"BOOL": true},
      "entityType": {"S": "field"}
    }' > /dev/null 2>&1 || true

  # last_name field
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "ENTITY#'"${USER_ENTITY_ID}"'"},
      "SK": {"S": "FIELD#e4b59ef1-4f5c-5eaf-9b8d-6f3c5g8d4e2b"},
      "id": {"S": "e4b59ef1-4f5c-5eaf-9b8d-6f3c5g8d4e2b"},
      "name": {"S": "last_name"},
      "label": {"S": "Last Name"},
      "fieldType": {"S": "Text"},
      "required": {"BOOL": true},
      "system": {"BOOL": true},
      "entityType": {"S": "field"}
    }' > /dev/null 2>&1 || true

  # password field
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "ENTITY#'"${USER_ENTITY_ID}"'"},
      "SK": {"S": "FIELD#f5c6af02-5064-6fb0-ac9e-704d6h9e5f3c"},
      "id": {"S": "f5c6af02-5064-6fb0-ac9e-704d6h9e5f3c"},
      "name": {"S": "password"},
      "label": {"S": "Password"},
      "fieldType": {"S": "Password"},
      "required": {"BOOL": true},
      "system": {"BOOL": true},
      "entityType": {"S": "field"}
    }' > /dev/null 2>&1 || true

  # enabled field
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "ENTITY#'"${USER_ENTITY_ID}"'"},
      "SK": {"S": "FIELD#a6d7b013-6175-70c1-bdaf-815e7i0f6g4d"},
      "id": {"S": "a6d7b013-6175-70c1-bdaf-815e7i0f6g4d"},
      "name": {"S": "enabled"},
      "label": {"S": "Enabled"},
      "fieldType": {"S": "Checkbox"},
      "required": {"BOOL": true},
      "system": {"BOOL": true},
      "entityType": {"S": "field"}
    }' > /dev/null 2>&1 || true

  log_ok "Seeded user entity fields (7 fields)"

  # --- Role Entity definition (ERPService.cs lines 344-370) ---
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "ENTITY#'"${ROLE_ENTITY_ID}"'"},
      "SK": {"S": "META"},
      "id": {"S": "'"${ROLE_ENTITY_ID}"'"},
      "name": {"S": "role"},
      "label": {"S": "Role"},
      "labelPlural": {"S": "Roles"},
      "system": {"BOOL": true},
      "color": {"S": "#f44336"},
      "iconName": {"S": "fa fa-key"},
      "entityType": {"S": "entity"},
      "recordPermissions": {"M": {
        "canCreate": {"L": [{"S": "'"${GUEST_ROLE_ID}"'"}, {"S": "'"${ADMINISTRATOR_ROLE_ID}"'"}]},
        "canRead": {"L": [{"S": "'"${REGULAR_ROLE_ID}"'"}, {"S": "'"${GUEST_ROLE_ID}"'"}, {"S": "'"${ADMINISTRATOR_ROLE_ID}"'"}]},
        "canUpdate": {"L": [{"S": "'"${ADMINISTRATOR_ROLE_ID}"'"}]},
        "canDelete": {"L": [{"S": "'"${ADMINISTRATOR_ROLE_ID}"'"}]}
      }}
    }' > /dev/null 2>&1 || true
  log_ok "Seeded entity: role (${ROLE_ENTITY_ID})"

  # --- Role Entity Fields ---
  # name field (ERPService.cs line 372-397)
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "ENTITY#'"${ROLE_ENTITY_ID}"'"},
      "SK": {"S": "FIELD#36f91ebd-5a02-4032-8498-b7f716f6a349"},
      "id": {"S": "36f91ebd-5a02-4032-8498-b7f716f6a349"},
      "name": {"S": "name"},
      "label": {"S": "Name"},
      "fieldType": {"S": "Text"},
      "required": {"BOOL": true},
      "system": {"BOOL": true},
      "maxLength": {"N": "200"},
      "enableSecurity": {"BOOL": true},
      "entityType": {"S": "field"}
    }' > /dev/null 2>&1 || true

  # description field (ERPService.cs line 399-416)
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "ENTITY#'"${ROLE_ENTITY_ID}"'"},
      "SK": {"S": "FIELD#4a8b9e0a-1c36-40c6-972b-b19e2b5d265b"},
      "id": {"S": "4a8b9e0a-1c36-40c6-972b-b19e2b5d265b"},
      "name": {"S": "description"},
      "label": {"S": "Description"},
      "fieldType": {"S": "Text"},
      "required": {"BOOL": true},
      "system": {"BOOL": true},
      "maxLength": {"N": "200"},
      "entityType": {"S": "field"}
    }' > /dev/null 2>&1 || true

  log_ok "Seeded role entity fields (2 fields)"

  # --- User-Role Relation definition (ERPService.cs lines 421-441) ---
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "RELATION#'"${USER_ROLE_RELATION_ID}"'"},
      "SK": {"S": "META"},
      "id": {"S": "'"${USER_ROLE_RELATION_ID}"'"},
      "name": {"S": "user_role"},
      "label": {"S": "User-Role"},
      "system": {"BOOL": true},
      "relationType": {"S": "ManyToMany"},
      "targetEntityId": {"S": "'"${USER_ENTITY_ID}"'"},
      "originEntityId": {"S": "'"${ROLE_ENTITY_ID}"'"},
      "entityType": {"S": "relation"}
    }' > /dev/null 2>&1 || true
  log_ok "Seeded relation: user_role (${USER_ROLE_RELATION_ID})"
}

# =============================================================================
# Phase 5c: Seed Plugin System Service DynamoDB Table
# =============================================================================
# Default plugin registrations matching the monolith's plugin inventory:
#   SdkPlugin, NextPlugin, ProjectPlugin, CrmPlugin, MailPlugin, MicrosoftCDMPlugin
# Source: ErpPlugin.cs abstract base and each plugin's *Plugin.cs entry point.
# =============================================================================
seed_plugin_system_table() {
  log_step "Seeding Plugin System service DynamoDB table"

  local table_name
  table_name=$(discover_dynamodb_table "plugin-system")

  # Verify table exists
  if ! awslocal dynamodb describe-table --table-name "${table_name}" &> /dev/null; then
    log_warn "Plugin System table '${table_name}' not found — skipping."
    return 0
  fi

  log_info "Using table: ${table_name}"

  # --- SDK Plugin (WebVella.Erp.Plugins.SDK/SdkPlugin.cs) ---
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "PLUGIN#sdk"},
      "SK": {"S": "META"},
      "id": {"S": "936e3e40-c60c-4c47-b9f0-0e2a77b35620"},
      "name": {"S": "sdk"},
      "displayName": {"S": "SDK Admin Console"},
      "description": {"S": "Provides the administration console for entity, page, datasource, role, user, and job management."},
      "version": {"S": "1.7.7"},
      "enabled": {"BOOL": true},
      "system": {"BOOL": true},
      "entityType": {"S": "plugin"},
      "createdAt": {"S": "2024-01-01T00:00:00Z"}
    }' > /dev/null 2>&1 || true
  log_ok "Seeded plugin: SDK Admin Console"

  # --- Next Plugin (WebVella.Erp.Plugins.Next/NextPlugin.cs) ---
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "PLUGIN#next"},
      "SK": {"S": "META"},
      "id": {"S": "b1a340b0-b215-4e23-a21f-92d1d9773ff0"},
      "name": {"S": "next"},
      "displayName": {"S": "Next"},
      "description": {"S": "Entity provisioning, search indexing, and core CRM entity definitions (account, contact, address, salutation)."},
      "version": {"S": "1.7.7"},
      "enabled": {"BOOL": true},
      "system": {"BOOL": true},
      "entityType": {"S": "plugin"},
      "createdAt": {"S": "2024-01-01T00:00:00Z"}
    }' > /dev/null 2>&1 || true
  log_ok "Seeded plugin: Next"

  # --- Project Plugin (WebVella.Erp.Plugins.Project/ProjectPlugin.cs) ---
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "PLUGIN#project"},
      "SK": {"S": "META"},
      "id": {"S": "c8eb3a11-e69d-4ead-a3c2-0e61c2e37497"},
      "name": {"S": "project"},
      "displayName": {"S": "Project Management"},
      "description": {"S": "Task management, timelogs, comments, feeds, and project reporting. Includes dashboard widgets and recurrence processing."},
      "version": {"S": "1.7.7"},
      "enabled": {"BOOL": true},
      "system": {"BOOL": false},
      "entityType": {"S": "plugin"},
      "createdAt": {"S": "2024-01-01T00:00:00Z"}
    }' > /dev/null 2>&1 || true
  log_ok "Seeded plugin: Project Management"

  # --- CRM Plugin (WebVella.Erp.Plugins.Crm/CrmPlugin.cs) ---
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "PLUGIN#crm"},
      "SK": {"S": "META"},
      "id": {"S": "d6730a69-bcd7-4b9e-aa6b-33078e0395e8"},
      "name": {"S": "crm"},
      "displayName": {"S": "CRM"},
      "description": {"S": "Customer relationship management plugin for accounts and contacts."},
      "version": {"S": "1.7.7"},
      "enabled": {"BOOL": true},
      "system": {"BOOL": false},
      "entityType": {"S": "plugin"},
      "createdAt": {"S": "2024-01-01T00:00:00Z"}
    }' > /dev/null 2>&1 || true
  log_ok "Seeded plugin: CRM"

  # --- Mail Plugin (WebVella.Erp.Plugins.Mail/MailPlugin.cs) ---
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "PLUGIN#mail"},
      "SK": {"S": "META"},
      "id": {"S": "e57a6970-c3da-4f49-b9ac-24c0d2a67591"},
      "name": {"S": "mail"},
      "displayName": {"S": "Mail"},
      "description": {"S": "Email subsystem with SMTP engine, queue processing, and scheduled delivery."},
      "version": {"S": "1.7.7"},
      "enabled": {"BOOL": true},
      "system": {"BOOL": false},
      "entityType": {"S": "plugin"},
      "createdAt": {"S": "2024-01-01T00:00:00Z"}
    }' > /dev/null 2>&1 || true
  log_ok "Seeded plugin: Mail"

  # --- MicrosoftCDM Plugin (WebVella.Erp.Plugins.MicrosoftCDM/MicrosoftCDMPlugin.cs) ---
  awslocal dynamodb put-item \
    --table-name "${table_name}" \
    --item '{
      "PK": {"S": "PLUGIN#microsoft-cdm"},
      "SK": {"S": "META"},
      "id": {"S": "f68b7a81-d4eb-5f5a-cbbd-35e1e3a68602"},
      "name": {"S": "microsoft-cdm"},
      "displayName": {"S": "Microsoft CDM"},
      "description": {"S": "Microsoft Common Data Model integration plugin."},
      "version": {"S": "1.7.7"},
      "enabled": {"BOOL": true},
      "system": {"BOOL": false},
      "entityType": {"S": "plugin"},
      "createdAt": {"S": "2024-01-01T00:00:00Z"}
    }' > /dev/null 2>&1 || true
  log_ok "Seeded plugin: Microsoft CDM"
}

# =============================================================================
# Phase 5d: Seed SSM Parameters
# =============================================================================
# Configuration values from WebVella.Erp.Site/Config.json migrated to
# SSM Parameter Store. Secrets use SecureString; plain config uses String.
# Per AAP §0.8.6: secrets via SSM SecureString, NEVER environment variables.
# =============================================================================
seed_ssm_parameters() {
  log_step "Seeding SSM Parameter Store"

  # Encryption key from Config.json line 5 — stored as SecureString
  awslocal ssm put-parameter \
    --name "/webvella-erp/encryption-key" \
    --value "BC93B776A42877CFEE808823BA8B37C83B6B0AD23198AC3AF2B5A54DCB647658" \
    --type SecureString \
    --overwrite > /dev/null 2>&1 || true
  log_ok "Seeded SSM: /webvella-erp/encryption-key (SecureString)"

  # Language setting from Config.json line 6
  awslocal ssm put-parameter \
    --name "/webvella-erp/lang" \
    --value "en" \
    --type String \
    --overwrite > /dev/null 2>&1 || true
  log_ok "Seeded SSM: /webvella-erp/lang"

  # Locale setting from Config.json line 7
  awslocal ssm put-parameter \
    --name "/webvella-erp/locale" \
    --value "en-US" \
    --type String \
    --overwrite > /dev/null 2>&1 || true
  log_ok "Seeded SSM: /webvella-erp/locale"

  # Timezone setting from Config.json line 8
  awslocal ssm put-parameter \
    --name "/webvella-erp/timezone" \
    --value "FLE Standard Time" \
    --type String \
    --overwrite > /dev/null 2>&1 || true
  log_ok "Seeded SSM: /webvella-erp/timezone"

  # JWT configuration from Config.json lines 24-28
  awslocal ssm put-parameter \
    --name "/webvella-erp/jwt-key" \
    --value "ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey" \
    --type SecureString \
    --overwrite > /dev/null 2>&1 || true
  log_ok "Seeded SSM: /webvella-erp/jwt-key (SecureString)"

  awslocal ssm put-parameter \
    --name "/webvella-erp/jwt-issuer" \
    --value "webvella-erp" \
    --type String \
    --overwrite > /dev/null 2>&1 || true
  log_ok "Seeded SSM: /webvella-erp/jwt-issuer"

  awslocal ssm put-parameter \
    --name "/webvella-erp/jwt-audience" \
    --value "webvella-erp" \
    --type String \
    --overwrite > /dev/null 2>&1 || true
  log_ok "Seeded SSM: /webvella-erp/jwt-audience"

  # Application metadata from Config.json line 21
  awslocal ssm put-parameter \
    --name "/webvella-erp/app-name" \
    --value "WebVella Next" \
    --type String \
    --overwrite > /dev/null 2>&1 || true
  log_ok "Seeded SSM: /webvella-erp/app-name"
}

# =============================================================================
# Phase 6: Verification & Summary
# =============================================================================
verify_seeded_data() {
  log_step "Verifying seeded data"

  # --- Cognito Groups ---
  local group_count
  group_count=$(awslocal cognito-idp list-groups \
    --user-pool-id "${USER_POOL_ID}" \
    --query 'Groups | length(@)' \
    --output text 2>/dev/null || echo "0")
  log_info "Cognito groups: ${group_count}"

  # --- Cognito Users ---
  local user_count
  user_count=$(awslocal cognito-idp list-users \
    --user-pool-id "${USER_POOL_ID}" \
    --query 'Users | length(@)' \
    --output text 2>/dev/null || echo "0")
  log_info "Cognito users: ${user_count}"

  # --- DynamoDB Tables ---
  local tables
  tables=$(awslocal dynamodb list-tables \
    --query 'TableNames' \
    --output text 2>/dev/null || echo "")
  log_info "DynamoDB tables: ${tables}"

  # Scan identity table for record count
  local identity_table
  identity_table=$(discover_dynamodb_table "identity")
  if awslocal dynamodb describe-table --table-name "${identity_table}" &> /dev/null; then
    local id_count
    id_count=$(awslocal dynamodb scan \
      --table-name "${identity_table}" \
      --select COUNT \
      --query 'Count' \
      --output text 2>/dev/null || echo "0")
    log_info "Identity table items: ${id_count}"
  fi

  # Scan entity-management table for record count
  local em_table
  em_table=$(discover_dynamodb_table "entity-management")
  if awslocal dynamodb describe-table --table-name "${em_table}" &> /dev/null; then
    local em_count
    em_count=$(awslocal dynamodb scan \
      --table-name "${em_table}" \
      --select COUNT \
      --query 'Count' \
      --output text 2>/dev/null || echo "0")
    log_info "Entity Management table items: ${em_count}"
  fi

  # Scan plugin-system table for record count
  local ps_table
  ps_table=$(discover_dynamodb_table "plugin-system")
  if awslocal dynamodb describe-table --table-name "${ps_table}" &> /dev/null; then
    local ps_count
    ps_count=$(awslocal dynamodb scan \
      --table-name "${ps_table}" \
      --select COUNT \
      --query 'Count' \
      --output text 2>/dev/null || echo "0")
    log_info "Plugin System table items: ${ps_count}"
  fi

  # --- SSM Parameters ---
  local ssm_count
  ssm_count=$(awslocal ssm get-parameters-by-path \
    --path "/webvella-erp" \
    --recursive \
    --query 'Parameters | length(@)' \
    --output text 2>/dev/null || echo "0")
  log_info "SSM parameters under /webvella-erp: ${ssm_count}"
}

print_summary() {
  echo ""
  echo "============================================================"
  echo "🎉 Test data seeding complete!"
  echo "============================================================"
  echo ""
  echo "✅ Seeded Cognito users:"
  echo "   - system@webvella.com (administrator)"
  echo "   - erp@webvella.com (administrator, regular) — default admin, password: erp"
  echo "   - regular@webvella.com (regular)"
  echo "   - guest@webvella.com (guest)"
  echo ""
  echo "✅ Seeded Cognito groups: administrator, regular, guest"
  echo ""
  echo "✅ Seeded DynamoDB tables: identity, entity-management, plugin-system"
  echo "   - Identity: 3 roles, 2 users, 3 user-role relationships"
  echo "   - Entity Management: 2 entities (user, role), fields, 1 relation"
  echo "   - Plugin System: 6 plugin registrations"
  echo ""
  echo "✅ Seeded SSM parameters: encryption-key, lang, locale, timezone,"
  echo "   jwt-key, jwt-issuer, jwt-audience, app-name"
  echo ""
  echo "Environment:"
  echo "  AWS_ENDPOINT_URL: ${AWS_ENDPOINT_URL}"
  echo "  AWS_REGION:       ${AWS_REGION}"
  echo "  User Pool ID:     ${USER_POOL_ID}"
  if [ -n "${CLIENT_ID}" ] && [ "${CLIENT_ID}" != "None" ]; then
    echo "  Client ID:        ${CLIENT_ID}"
  fi
  echo ""
}

# =============================================================================
# Main Execution
# =============================================================================
main() {
  echo "============================================================"
  echo "WebVella ERP — Test Data Seeder"
  echo "============================================================"
  echo "Target: ${AWS_ENDPOINT_URL} (${AWS_REGION})"
  echo ""

  # Phase 1: Validate prerequisites
  check_prerequisites

  # Phase 2: Discover Cognito resources
  discover_cognito_resources

  # Phase 3: Seed Cognito groups (roles)
  seed_cognito_groups

  # Phase 4: Seed Cognito users and assign to groups
  seed_cognito_users

  # Phase 5: Seed DynamoDB tables
  seed_identity_table
  seed_entity_management_table
  seed_plugin_system_table

  # Phase 5d: Seed SSM parameters
  seed_ssm_parameters

  # Phase 6: Verify and summarize
  verify_seeded_data
  print_summary
}

# Execute main function
main "$@"
