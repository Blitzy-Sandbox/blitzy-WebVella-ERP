#!/usr/bin/env bash
# =============================================================================
# WebVella ERP — FluentMigrator Database Migration Runner
# =============================================================================
# Executes FluentMigrator migrations for the two RDS PostgreSQL-backed
# bounded-context services: Invoicing and Reporting.
#
# This script replaces the monolith's startup-time DDL operations:
#   - DbRepository.cs: CreatePostgresqlExtensions(), CreatePostgresqlCasts(),
#     CreateTable() (lines 13-70)
#   - ERPService.cs: CheckCreateSystemTables() (lines 922-1159) which created
#     entities, entity_relations, system_settings, system_search, files, jobs,
#     schedule_plan, and system_log tables at application startup.
#
# In the target serverless architecture, only Invoicing and Reporting use
# RDS PostgreSQL (all other services use DynamoDB and need no migrations).
# FluentMigrator tracks applied migrations in a VersionInfo table, making
# this script fully idempotent — safe to run multiple times.
#
# Prerequisites:
#   1. Docker running with LocalStack container (docker compose up -d)
#   2. CDK stacks deployed (./tools/scripts/bootstrap-localstack.sh)
#   3. .NET 9 SDK installed (dotnet CLI)
#   4. awslocal CLI installed (pip install awscli-local)
#   5. FluentMigrator CLI installed (dotnet tool install -g FluentMigrator.DotNet.Cli)
#
# Usage:
#   ./tools/scripts/run-migrations.sh                    # Run all migrations
#   ./tools/scripts/run-migrations.sh --service invoicing # Run invoicing only
#   ./tools/scripts/run-migrations.sh --service reporting # Run reporting only
#   ./tools/scripts/run-migrations.sh --rollback          # Rollback last batch
#   ./tools/scripts/run-migrations.sh --dry-run           # List pending migrations
#   ./tools/scripts/run-migrations.sh --help              # Show help
#
# Environment variables (all have sensible defaults for LocalStack):
#   AWS_ENDPOINT_URL      — LocalStack endpoint (default: http://localhost:4566)
#   AWS_REGION            — AWS region (default: us-east-1)
#   AWS_ACCESS_KEY_ID     — AWS access key (default: test)
#   AWS_SECRET_ACCESS_KEY — AWS secret key (default: test)
#   DOTNET_ROOT           — .NET SDK root path (default: /usr/local/dotnet)
#
# References:
#   AAP §0.5.1  — Invoicing uses RDS PostgreSQL with Npgsql + FluentMigrator
#   AAP §0.5.1  — Reporting uses RDS PostgreSQL for read-optimized projections
#   AAP §0.8.1  — LocalStack-exclusive testing
#   AAP §0.8.6  — DB connection strings stored as SSM SecureString
#   AAP §0.8.6  — AWS_ENDPOINT_URL=http://localhost:4566, AWS_REGION=us-east-1
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

# .NET SDK path configuration (per setup instructions)
export DOTNET_ROOT="${DOTNET_ROOT:-/usr/local/dotnet}"
export PATH="${DOTNET_ROOT}:${PATH}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# Script location resolution
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

# =============================================================================
# Service Configuration
# =============================================================================
# Only Invoicing and Reporting use RDS PostgreSQL (all others use DynamoDB).
# Assembly names match the <AssemblyName> in each .csproj file.
# SSM parameter paths follow AAP §0.8.6 convention.
# Default ports follow LocalStack external service port range (4510-4559).
# =============================================================================
readonly INVOICING_SERVICE_NAME="invoicing"
readonly INVOICING_CSPROJ="${REPO_ROOT}/services/invoicing/Invoicing.csproj"
readonly INVOICING_ASSEMBLY_NAME="WebVellaErp.Invoicing"
readonly INVOICING_ASSEMBLY_DIR="${REPO_ROOT}/services/invoicing/src/bin/Debug/net9.0"
readonly INVOICING_SSM_PARAM="/webvella-erp/invoicing/db-connection-string"
readonly INVOICING_DEFAULT_CONN="Host=localhost;Port=4510;Database=invoicing;Username=postgres;Password=postgres"
readonly INVOICING_DEFAULT_HOST="localhost"
readonly INVOICING_DEFAULT_PORT="4510"
readonly INVOICING_DEFAULT_DB="invoicing"
readonly INVOICING_DEFAULT_USER="postgres"

readonly REPORTING_SERVICE_NAME="reporting"
readonly REPORTING_CSPROJ="${REPO_ROOT}/services/reporting/Reporting.csproj"
readonly REPORTING_ASSEMBLY_NAME="WebVellaErp.Reporting"
readonly REPORTING_ASSEMBLY_DIR="${REPO_ROOT}/services/reporting/src/bin/Debug/net9.0"
readonly REPORTING_SSM_PARAM="/webvella-erp/reporting/db-connection-string"
readonly REPORTING_DEFAULT_CONN="Host=localhost;Port=4511;Database=reporting;Username=postgres;Password=postgres"
readonly REPORTING_DEFAULT_HOST="localhost"
readonly REPORTING_DEFAULT_PORT="4511"
readonly REPORTING_DEFAULT_DB="reporting"
readonly REPORTING_DEFAULT_USER="postgres"

# =============================================================================
# Logging Helpers (consistent with sibling scripts)
# =============================================================================
log_info()  { echo "ℹ️  $*"; }
log_ok()    { echo "✅ $*"; }
log_warn()  { echo "⚠️  $*"; }
log_error() { echo "❌ $*" >&2; }
log_step()  { echo ""; echo "━━━ $* ━━━"; }

# =============================================================================
# Prerequisites Validation
# =============================================================================
check_prerequisites() {
  log_step "Checking prerequisites"

  # Check core CLI tools
  if ! command -v curl &> /dev/null; then
    log_error "curl is not installed. Please install curl."
    exit 1
  fi
  log_ok "curl found"

  # Check .NET 9 SDK is installed and accessible
  if ! command -v dotnet &> /dev/null; then
    log_error "dotnet CLI is not installed or not in PATH."
    log_error "Install .NET 9 SDK and ensure DOTNET_ROOT is set correctly."
    log_error "Current DOTNET_ROOT: ${DOTNET_ROOT}"
    exit 1
  fi
  local dotnet_version
  dotnet_version=$(dotnet --version 2>/dev/null || echo "unknown")
  log_ok "dotnet CLI found (version: ${dotnet_version})"

  # Check FluentMigrator CLI tool (dotnet fm)
  if ! dotnet fm --version &> /dev/null; then
    log_warn "FluentMigrator CLI (dotnet fm) not found. Attempting to install..."
    if ! dotnet tool install -g FluentMigrator.DotNet.Cli 2>/dev/null; then
      log_error "Failed to install FluentMigrator CLI."
      log_error "Install manually: dotnet tool install -g FluentMigrator.DotNet.Cli"
      exit 1
    fi
    # Ensure the tool path is available in the current session
    export PATH="${HOME}/.dotnet/tools:${PATH}"
    if ! dotnet fm --version &> /dev/null; then
      log_error "FluentMigrator CLI installed but not accessible in PATH."
      log_error "Add ~/.dotnet/tools to your PATH and try again."
      exit 1
    fi
    log_ok "FluentMigrator CLI installed successfully"
  else
    log_ok "FluentMigrator CLI (dotnet fm) found"
  fi

  # Check awslocal CLI (from awscli-local pip package)
  if ! command -v awslocal &> /dev/null; then
    log_warn "awslocal CLI not found. SSM parameter discovery will fall back to defaults."
    log_warn "Install it with: pip install awscli-local"
  else
    log_ok "awslocal CLI found"
  fi

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

  # Check LocalStack container is running
  cd "${REPO_ROOT}"
  if ! docker compose ps 2>/dev/null | grep -q "localstack"; then
    log_warn "LocalStack container is not running."
    log_error "Start it with: cd ${REPO_ROOT} && docker compose up -d"
    log_error "Then deploy CDK stacks: ./tools/scripts/bootstrap-localstack.sh"
    exit 1
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
# Connection String Discovery
# =============================================================================
# Attempts SSM Parameter Store lookup first (per AAP §0.8.6 — connection
# strings stored as SSM SecureString), then falls back to LocalStack RDS
# defaults (ports 4510/4511) if SSM is unavailable.
# =============================================================================
discover_connection_string() {
  local service_name="$1"
  local ssm_param="$2"
  local default_conn="$3"

  log_info "Discovering connection string for ${service_name}..."

  # Attempt SSM Parameter Store lookup (preferred method per AAP §0.8.6)
  if command -v awslocal &> /dev/null; then
    local ssm_value
    ssm_value=$(awslocal ssm get-parameter \
      --name "${ssm_param}" \
      --with-decryption \
      --query 'Parameter.Value' \
      --output text 2>/dev/null || echo "")

    if [ -n "${ssm_value}" ] && [ "${ssm_value}" != "None" ] && [ "${ssm_value}" != "null" ]; then
      log_ok "Retrieved ${service_name} connection string from SSM: ${ssm_param}"
      echo "${ssm_value}"
      return 0
    fi

    log_warn "SSM parameter ${ssm_param} not found or empty. Using default connection string."
  else
    log_warn "awslocal not available. Using default connection string for ${service_name}."
  fi

  # Fallback to LocalStack RDS defaults
  log_info "Using default connection: ${default_conn}"
  echo "${default_conn}"
  return 0
}

# =============================================================================
# Build Service
# =============================================================================
# Builds the .NET 9 project to produce the assembly (DLL) containing
# FluentMigrator migration classes. The assembly is required by the
# dotnet fm CLI to discover and execute migrations.
# =============================================================================
build_service() {
  local service_name="$1"
  local csproj_path="$2"
  local assembly_dir="$3"
  local assembly_name="$4"

  log_step "Building ${service_name} service"

  if [ ! -f "${csproj_path}" ]; then
    log_error "Project file not found: ${csproj_path}"
    exit 1
  fi

  log_info "Building ${csproj_path}..."
  if ! dotnet build "${csproj_path}" --configuration Debug --verbosity quiet 2>&1; then
    log_error "Build failed for ${service_name} service."
    log_error "Fix build errors in ${csproj_path} before running migrations."
    exit 1
  fi

  local assembly_path="${assembly_dir}/${assembly_name}.dll"
  if [ ! -f "${assembly_path}" ]; then
    log_error "Assembly not found after build: ${assembly_path}"
    log_error "Expected assembly name: ${assembly_name}.dll"
    exit 1
  fi

  log_ok "${service_name} service built successfully (${assembly_name}.dll)"
}

# =============================================================================
# Run Migrations
# =============================================================================
# Executes FluentMigrator migrations against the target RDS PostgreSQL
# instance. FluentMigrator tracks applied migrations in a VersionInfo
# table, making this operation fully idempotent.
# =============================================================================
run_migrations() {
  local service_name="$1"
  local connection_string="$2"
  local assembly_dir="$3"
  local assembly_name="$4"
  local tag="$5"

  local assembly_path="${assembly_dir}/${assembly_name}.dll"

  log_step "Running ${service_name} migrations"

  if [ ! -f "${assembly_path}" ]; then
    log_error "Assembly not found: ${assembly_path}"
    log_error "Build the service first with: dotnet build"
    exit 1
  fi

  log_info "Executing migrations from ${assembly_name}.dll..."
  if ! dotnet fm migrate \
    --processor Postgres \
    --connection "${connection_string}" \
    --assembly "${assembly_path}" \
    --tag "${tag}" 2>&1; then
    log_error "Migration failed for ${service_name} service."
    return 1
  fi

  log_ok "${service_name} migrations completed successfully"
  return 0
}

# =============================================================================
# Rollback Migrations
# =============================================================================
# Rolls back the last migration batch for the specified service.
# Uses FluentMigrator's rollback command.
# =============================================================================
rollback_migrations() {
  local service_name="$1"
  local connection_string="$2"
  local assembly_dir="$3"
  local assembly_name="$4"
  local tag="$5"

  local assembly_path="${assembly_dir}/${assembly_name}.dll"

  log_step "Rolling back ${service_name} migrations"

  if [ ! -f "${assembly_path}" ]; then
    log_error "Assembly not found: ${assembly_path}"
    log_error "Build the service first with: dotnet build"
    exit 1
  fi

  log_info "Rolling back last migration batch from ${assembly_name}.dll..."
  if ! dotnet fm rollback \
    --processor Postgres \
    --connection "${connection_string}" \
    --assembly "${assembly_path}" \
    --tag "${tag}" 2>&1; then
    log_error "Rollback failed for ${service_name} service."
    return 1
  fi

  log_ok "${service_name} rollback completed successfully"
  return 0
}

# =============================================================================
# List Pending Migrations (Dry Run)
# =============================================================================
# Lists all pending (unapplied) migrations for the specified service
# without executing them. Useful for verifying migration state.
# =============================================================================
list_pending_migrations() {
  local service_name="$1"
  local connection_string="$2"
  local assembly_dir="$3"
  local assembly_name="$4"
  local tag="$5"

  local assembly_path="${assembly_dir}/${assembly_name}.dll"

  log_step "Listing pending migrations for ${service_name}"

  if [ ! -f "${assembly_path}" ]; then
    log_error "Assembly not found: ${assembly_path}"
    log_error "Build the service first with: dotnet build"
    exit 1
  fi

  log_info "Querying migration status for ${assembly_name}.dll..."
  if ! dotnet fm list migrations \
    --processor Postgres \
    --connection "${connection_string}" \
    --assembly "${assembly_path}" \
    --tag "${tag}" 2>&1; then
    log_warn "Could not list migrations for ${service_name}. Database may not be accessible."
    return 1
  fi

  return 0
}

# =============================================================================
# Verify Schema
# =============================================================================
# Post-migration verification step. Connects directly to RDS PostgreSQL
# via psql and lists created tables to confirm migration success.
# This is a non-critical informational step — failure does not abort.
# =============================================================================
verify_schema() {
  local service_name="$1"
  local host="$2"
  local port="$3"
  local database="$4"
  local username="$5"

  log_info "Verifying ${service_name} schema..."

  if ! command -v psql &> /dev/null; then
    log_warn "psql not found. Skipping schema verification for ${service_name}."
    log_warn "Install postgresql-client to enable verification."
    return 0
  fi

  local table_list
  table_list=$(PGPASSWORD=postgres psql \
    -h "${host}" \
    -p "${port}" \
    -U "${username}" \
    -d "${database}" \
    -c "\\dt ${service_name}.*" 2>/dev/null || echo "")

  if [ -n "${table_list}" ]; then
    log_ok "${service_name} schema verified — tables found:"
    echo "${table_list}"
  else
    log_warn "Could not verify ${service_name} schema (psql connection failed or no tables found)."
    log_warn "This may be expected if RDS is not fully provisioned in LocalStack."
  fi
  return 0
}

# =============================================================================
# Process Service (Build + Discover + Migrate/Rollback/DryRun + Verify)
# =============================================================================
# Orchestrates the full migration lifecycle for a single service:
#   1. Build the .NET project to produce the migration assembly
#   2. Discover the connection string (SSM → fallback to defaults)
#   3. Execute the requested operation (migrate, rollback, or dry-run)
#   4. Verify the resulting schema (informational only)
# =============================================================================
process_service() {
  local service_name="$1"
  local csproj_path="$2"
  local assembly_name="$3"
  local assembly_dir="$4"
  local ssm_param="$5"
  local default_conn="$6"
  local default_host="$7"
  local default_port="$8"
  local default_db="$9"
  local default_user="${10}"
  local operation="${11}"

  # Step 1: Build the service
  build_service "${service_name}" "${csproj_path}" "${assembly_dir}" "${assembly_name}"

  # Step 2: Discover connection string
  local conn_string
  conn_string=$(discover_connection_string "${service_name}" "${ssm_param}" "${default_conn}")

  # Step 3: Execute the requested operation
  case "${operation}" in
    migrate)
      run_migrations "${service_name}" "${conn_string}" "${assembly_dir}" "${assembly_name}" "${service_name}"
      ;;
    rollback)
      rollback_migrations "${service_name}" "${conn_string}" "${assembly_dir}" "${assembly_name}" "${service_name}"
      ;;
    dry-run)
      list_pending_migrations "${service_name}" "${conn_string}" "${assembly_dir}" "${assembly_name}" "${service_name}"
      ;;
    *)
      log_error "Unknown operation: ${operation}"
      exit 1
      ;;
  esac

  # Step 4: Verify schema (informational, non-blocking)
  if [ "${operation}" = "migrate" ]; then
    verify_schema "${service_name}" "${default_host}" "${default_port}" "${default_db}" "${default_user}"
  fi
}

# =============================================================================
# Process Invoicing Service
# =============================================================================
process_invoicing() {
  local operation="$1"
  process_service \
    "${INVOICING_SERVICE_NAME}" \
    "${INVOICING_CSPROJ}" \
    "${INVOICING_ASSEMBLY_NAME}" \
    "${INVOICING_ASSEMBLY_DIR}" \
    "${INVOICING_SSM_PARAM}" \
    "${INVOICING_DEFAULT_CONN}" \
    "${INVOICING_DEFAULT_HOST}" \
    "${INVOICING_DEFAULT_PORT}" \
    "${INVOICING_DEFAULT_DB}" \
    "${INVOICING_DEFAULT_USER}" \
    "${operation}"
}

# =============================================================================
# Process Reporting Service
# =============================================================================
process_reporting() {
  local operation="$1"
  process_service \
    "${REPORTING_SERVICE_NAME}" \
    "${REPORTING_CSPROJ}" \
    "${REPORTING_ASSEMBLY_NAME}" \
    "${REPORTING_ASSEMBLY_DIR}" \
    "${REPORTING_SSM_PARAM}" \
    "${REPORTING_DEFAULT_CONN}" \
    "${REPORTING_DEFAULT_HOST}" \
    "${REPORTING_DEFAULT_PORT}" \
    "${REPORTING_DEFAULT_DB}" \
    "${REPORTING_DEFAULT_USER}" \
    "${operation}"
}

# =============================================================================
# Usage / Help
# =============================================================================
show_help() {
  cat <<'EOF'
WebVella ERP — FluentMigrator Database Migration Runner

Executes FluentMigrator migrations for the two RDS PostgreSQL-backed services:
Invoicing and Reporting. All other services use DynamoDB (no migrations needed).

USAGE:
  run-migrations.sh [OPTIONS]

OPTIONS:
  --service <name>    Run migrations for a specific service only.
                      Valid values: invoicing, reporting
                      Default: run both services

  --rollback          Rollback the last migration batch instead of migrating.
                      Can be combined with --service to rollback a single service.

  --dry-run           Show pending migrations without executing them.
                      Can be combined with --service to check a single service.

  --help              Show this help message and exit.

EXAMPLES:
  # Run all pending migrations for both services
  ./tools/scripts/run-migrations.sh

  # Run migrations for invoicing only
  ./tools/scripts/run-migrations.sh --service invoicing

  # Run migrations for reporting only
  ./tools/scripts/run-migrations.sh --service reporting

  # Rollback last migration batch for both services
  ./tools/scripts/run-migrations.sh --rollback

  # Rollback last migration batch for invoicing only
  ./tools/scripts/run-migrations.sh --rollback --service invoicing

  # Preview pending migrations without applying (dry run)
  ./tools/scripts/run-migrations.sh --dry-run

  # Preview pending migrations for reporting only
  ./tools/scripts/run-migrations.sh --dry-run --service reporting

PREREQUISITES:
  1. Docker running with LocalStack container:
       docker compose up -d

  2. CDK stacks deployed (creates RDS PostgreSQL instances):
       ./tools/scripts/bootstrap-localstack.sh

  3. .NET 9 SDK installed (dotnet CLI)

  4. FluentMigrator CLI installed:
       dotnet tool install -g FluentMigrator.DotNet.Cli

  5. awslocal CLI installed (optional, for SSM parameter discovery):
       pip install awscli-local

ENVIRONMENT VARIABLES:
  AWS_ENDPOINT_URL      LocalStack endpoint (default: http://localhost:4566)
  AWS_REGION            AWS region (default: us-east-1)
  AWS_ACCESS_KEY_ID     AWS access key (default: test)
  AWS_SECRET_ACCESS_KEY AWS secret key (default: test)
  DOTNET_ROOT           .NET SDK root path (default: /usr/local/dotnet)

NOTES:
  - Migrations are idempotent — FluentMigrator tracks applied migrations
    in a VersionInfo table, so re-running is always safe.
  - Connection strings are discovered from SSM Parameter Store first,
    then fall back to LocalStack RDS defaults (ports 4510/4511).
  - Only Invoicing and Reporting use RDS PostgreSQL; all other services
    use DynamoDB and do not require database migrations.
EOF
}

# =============================================================================
# CLI Argument Parsing
# =============================================================================
parse_arguments() {
  TARGET_SERVICE="all"
  OPERATION="migrate"

  while [ $# -gt 0 ]; do
    case "$1" in
      --service)
        if [ $# -lt 2 ]; then
          log_error "--service requires a value: invoicing or reporting"
          exit 1
        fi
        TARGET_SERVICE="$2"
        case "${TARGET_SERVICE}" in
          invoicing|reporting)
            ;;
          *)
            log_error "Invalid service name: ${TARGET_SERVICE}"
            log_error "Valid values: invoicing, reporting"
            exit 1
            ;;
        esac
        shift 2
        ;;
      --rollback)
        OPERATION="rollback"
        shift
        ;;
      --dry-run)
        OPERATION="dry-run"
        shift
        ;;
      --help|-h)
        show_help
        exit 0
        ;;
      *)
        log_error "Unknown option: $1"
        log_error "Use --help for usage information."
        exit 1
        ;;
    esac
  done
}

# =============================================================================
# Main Entry Point
# =============================================================================
main() {
  parse_arguments "$@"

  echo "============================================================================="
  echo "  WebVella ERP — Database Migration Runner"
  echo "============================================================================="
  echo "  Operation:    ${OPERATION}"
  echo "  Target:       ${TARGET_SERVICE}"
  echo "  Endpoint:     ${AWS_ENDPOINT_URL}"
  echo "  Region:       ${AWS_REGION}"
  echo "  Repo root:    ${REPO_ROOT}"
  echo "============================================================================="

  # Validate prerequisites
  check_prerequisites

  # Track overall success
  local exit_code=0

  # Execute migrations based on target service selection
  case "${TARGET_SERVICE}" in
    invoicing)
      process_invoicing "${OPERATION}" || exit_code=$?
      ;;
    reporting)
      process_reporting "${OPERATION}" || exit_code=$?
      ;;
    all)
      # Run both services sequentially — continue with reporting even if invoicing fails
      log_step "Processing all RDS PostgreSQL services"

      process_invoicing "${OPERATION}" || {
        log_error "Invoicing ${OPERATION} failed."
        exit_code=1
      }

      process_reporting "${OPERATION}" || {
        log_error "Reporting ${OPERATION} failed."
        exit_code=1
      }
      ;;
  esac

  # Summary
  echo ""
  echo "============================================================================="
  if [ "${exit_code}" -eq 0 ]; then
    case "${OPERATION}" in
      migrate)
        case "${TARGET_SERVICE}" in
          invoicing)
            log_ok "Invoicing service migrations completed successfully"
            ;;
          reporting)
            log_ok "Reporting service migrations completed successfully"
            ;;
          all)
            log_ok "Invoicing service migrations completed successfully"
            log_ok "Reporting service migrations completed successfully"
            log_ok "All database migrations applied"
            ;;
        esac
        ;;
      rollback)
        log_ok "Rollback completed successfully for: ${TARGET_SERVICE}"
        ;;
      dry-run)
        log_ok "Dry run completed for: ${TARGET_SERVICE}"
        ;;
    esac
  else
    log_error "Migration operation '${OPERATION}' failed for one or more services."
    log_error "Review the output above for details."
  fi
  echo "============================================================================="

  return "${exit_code}"
}

# Execute main only when the script is run directly (not sourced)
main "$@"
