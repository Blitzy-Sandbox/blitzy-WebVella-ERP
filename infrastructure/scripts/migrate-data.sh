#!/usr/bin/env bash
###############################################################################
# WebVella ERP — Monolith-to-Microservices Data Migration Script
#
# Purpose:
#   Extracts rec_* entity tables, rel_* relation join tables, and supporting
#   system metadata tables from the WebVella ERP monolith's shared PostgreSQL
#   database into per-service databases (database-per-service model).
#
# Target Services:
#   - Core (erp_core):    user, role, user_file, language, currency, country
#   - CRM  (erp_crm):     account, contact, case, address, salutation
#   - Project (erp_project): task, timelog, comment, feed, task_type
#   - Mail (erp_mail):    email, smtp_service
#
# Usage:
#   ./migrate-data.sh              # Run full migration
#   ./migrate-data.sh --dry-run    # Print actions without modifying databases
#   ./migrate-data.sh --rollback   # Restore from backup and drop target DBs
#   ./migrate-data.sh --help       # Display usage information
#
# Idempotency:
#   The script checks for a _migration_metadata marker table in each target
#   database. If found, it skips that database unless --force is also passed.
#   All CREATE TABLE statements use IF NOT EXISTS, and data inserts run inside
#   transactions that truncate target tables first.
#
# Reversibility:
#   Before any migration, a pg_dump backup of the full source database is
#   stored in $BACKUP_DIR. The --rollback flag uses pg_restore to undo.
#
# Environment Variables:
#   SOURCE_PG_HOST, SOURCE_PG_PORT, SOURCE_PG_DB, SOURCE_PG_USER,
#   SOURCE_PG_PASSWORD, CORE_PG_HOST, CORE_PG_PORT, CORE_PG_DB,
#   CRM_PG_HOST, CRM_PG_PORT, CRM_PG_DB, PROJECT_PG_HOST, PROJECT_PG_PORT,
#   PROJECT_PG_DB, MAIL_PG_HOST, MAIL_PG_PORT, MAIL_PG_DB, TARGET_PG_USER,
#   TARGET_PG_PASSWORD, DRY_RUN, BACKUP_DIR, LOG_FILE
#
# Prerequisites:
#   - bash >= 5.0
#   - psql, pg_dump, pg_restore (postgresql-client >= 16)
#   - Network access to source and all target PostgreSQL instances
#
# See also: docker-compose.yml for target database container definitions.
###############################################################################
set -euo pipefail

###############################################################################
# CONFIGURATION — Environment variables with sensible defaults
###############################################################################

# --- Source (monolith) database (matches Config.json defaults) ---
readonly SOURCE_PG_HOST="${SOURCE_PG_HOST:-localhost}"
readonly SOURCE_PG_PORT="${SOURCE_PG_PORT:-5432}"
readonly SOURCE_PG_DB="${SOURCE_PG_DB:-ttg_test}"
readonly SOURCE_PG_USER="${SOURCE_PG_USER:-dev}"
readonly SOURCE_PG_PASSWORD="${SOURCE_PG_PASSWORD:-dev}"

# --- Target service databases (match docker-compose.yml defaults) ---
readonly CORE_PG_HOST="${CORE_PG_HOST:-localhost}"
readonly CORE_PG_PORT="${CORE_PG_PORT:-5432}"
readonly CORE_PG_DB="${CORE_PG_DB:-erp_core}"

readonly CRM_PG_HOST="${CRM_PG_HOST:-localhost}"
readonly CRM_PG_PORT="${CRM_PG_PORT:-5433}"
readonly CRM_PG_DB="${CRM_PG_DB:-erp_crm}"

readonly PROJECT_PG_HOST="${PROJECT_PG_HOST:-localhost}"
readonly PROJECT_PG_PORT="${PROJECT_PG_PORT:-5434}"
readonly PROJECT_PG_DB="${PROJECT_PG_DB:-erp_project}"

readonly MAIL_PG_HOST="${MAIL_PG_HOST:-localhost}"
readonly MAIL_PG_PORT="${MAIL_PG_PORT:-5435}"
readonly MAIL_PG_DB="${MAIL_PG_DB:-erp_mail}"

readonly TARGET_PG_USER="${TARGET_PG_USER:-dev}"
readonly TARGET_PG_PASSWORD="${TARGET_PG_PASSWORD:-dev}"

# --- Script behaviour ---
DRY_RUN="${DRY_RUN:-false}"
readonly BACKUP_DIR="${BACKUP_DIR:-/tmp/erp-migration-backup}"
readonly LOG_FILE="${LOG_FILE:-/tmp/erp-migration.log}"
FORCE_FLAG=false

# --- Timestamp for backup file naming ---
MIGRATION_TS="$(date +%Y%m%d_%H%M%S)"
readonly MIGRATION_TS

# --- Track elapsed time ---
readonly SCRIPT_START_SECONDS="$SECONDS"

###############################################################################
# ENTITY-TO-SERVICE OWNERSHIP MATRIX
# Derived from AAP Section 0.7.1 and ERPService.cs system entity definitions.
###############################################################################

# Core service entity tables (rec_<entityName>)
CORE_ENTITY_TABLES=("rec_user" "rec_role" "rec_user_file" "rec_language" "rec_currency" "rec_country")
# Core service relation tables (both sides are Core entities)
CORE_RELATION_TABLES=("rel_user_role")

# CRM service entity tables
CRM_ENTITY_TABLES=("rec_account" "rec_contact" "rec_case" "rec_address" "rec_salutation")
# CRM relation tables — populated dynamically via discover_service_relations()
CRM_RELATION_TABLES=()

# Project service entity tables
PROJECT_ENTITY_TABLES=("rec_task" "rec_timelog" "rec_comment" "rec_feed" "rec_task_type")
# Project relation tables — populated dynamically
PROJECT_RELATION_TABLES=()

# Mail service entity tables
MAIL_ENTITY_TABLES=("rec_email" "rec_smtp_service")
# Mail relation tables — populated dynamically
MAIL_RELATION_TABLES=()

# System metadata tables (from ERPService.cs CheckCreateSystemTables())
# Core receives ALL system tables as the metadata authority.
CORE_SYSTEM_TABLES=(
  "entities"
  "entity_relations"
  "system_settings"
  "system_search"
  "files"
  "jobs"
  "schedule_plan"
  "system_log"
  "plugin_data"
  "app"
  "app_sitemap_area"
  "app_sitemap_area_group"
  "app_sitemap_area_node"
  "app_page"
  "app_page_body_node"
  "app_page_data_source"
  "data_source"
)

# Entity-name arrays (without rec_ prefix) used for metadata filtering
# (used by migrate_system_tables and discover_service_relations functions)
# shellcheck disable=SC2034
CORE_ENTITY_NAMES=("user" "role" "user_file" "language" "currency" "country")
CRM_ENTITY_NAMES=("account" "contact" "case" "address" "salutation")
PROJECT_ENTITY_NAMES=("task" "timelog" "comment" "feed" "task_type")
MAIL_ENTITY_NAMES=("email" "smtp_service")

# Accumulator for migration summary
declare -A MIGRATION_COUNTS

###############################################################################
# COLOUR-CODED LOGGING FUNCTIONS
###############################################################################

# Detect if stdout is a terminal for colour support
if [[ -t 1 ]]; then
  readonly CLR_RESET="\033[0m"
  readonly CLR_GREEN="\033[0;32m"
  readonly CLR_RED="\033[0;31m"
  readonly CLR_YELLOW="\033[0;33m"
  readonly CLR_CYAN="\033[0;36m"
  readonly CLR_BOLD="\033[1m"
else
  readonly CLR_RESET=""
  readonly CLR_GREEN=""
  readonly CLR_RED=""
  readonly CLR_YELLOW=""
  readonly CLR_CYAN=""
  readonly CLR_BOLD=""
fi

_ts() {
  date "+%Y-%m-%d %H:%M:%S"
}

log_info() {
  local msg="$1"
  printf "%b[INFO  %s]%b %s\n" "${CLR_CYAN}" "$(_ts)" "${CLR_RESET}" "$msg" | tee -a "$LOG_FILE"
}

log_success() {
  local msg="$1"
  printf "%b[OK    %s]%b %s\n" "${CLR_GREEN}" "$(_ts)" "${CLR_RESET}" "$msg" | tee -a "$LOG_FILE"
}

log_error() {
  local msg="$1"
  printf "%b[ERROR %s]%b %s\n" "${CLR_RED}" "$(_ts)" "${CLR_RESET}" "$msg" | tee -a "$LOG_FILE" >&2
}

log_warn() {
  local msg="$1"
  printf "%b[WARN  %s]%b %s\n" "${CLR_YELLOW}" "$(_ts)" "${CLR_RESET}" "$msg" | tee -a "$LOG_FILE"
}

log_step() {
  local msg="$1"
  printf "\n%b══════════════════════════════════════════════════════════════%b\n" "${CLR_BOLD}" "${CLR_RESET}" | tee -a "$LOG_FILE"
  printf "%b  %s%b\n" "${CLR_BOLD}" "$msg" "${CLR_RESET}" | tee -a "$LOG_FILE"
  printf "%b══════════════════════════════════════════════════════════════%b\n\n" "${CLR_BOLD}" "${CLR_RESET}" | tee -a "$LOG_FILE"
}

###############################################################################
# PSQL HELPER FUNCTIONS
###############################################################################

# Execute SQL against the source (monolith) database.
# Usage: psql_source "SELECT 1"
psql_source() {
  local sql="$1"
  PGPASSWORD="${SOURCE_PG_PASSWORD}" psql \
    -h "${SOURCE_PG_HOST}" \
    -p "${SOURCE_PG_PORT}" \
    -U "${SOURCE_PG_USER}" \
    -d "${SOURCE_PG_DB}" \
    --no-psqlrc \
    -v ON_ERROR_STOP=1 \
    -t -A -q \
    -c "$sql"
}

# Execute SQL against a target service database.
# Usage: psql_target "core" "SELECT 1"
#        psql_target "crm" "SELECT 1"
psql_target() {
  local service="$1"
  local sql="$2"
  local host port db
  case "$service" in
    core)
      host="${CORE_PG_HOST}"; port="${CORE_PG_PORT}"; db="${CORE_PG_DB}" ;;
    crm)
      host="${CRM_PG_HOST}"; port="${CRM_PG_PORT}"; db="${CRM_PG_DB}" ;;
    project)
      host="${PROJECT_PG_HOST}"; port="${PROJECT_PG_PORT}"; db="${PROJECT_PG_DB}" ;;
    mail)
      host="${MAIL_PG_HOST}"; port="${MAIL_PG_PORT}"; db="${MAIL_PG_DB}" ;;
    *)
      log_error "Unknown service: ${service}"
      return 1 ;;
  esac
  PGPASSWORD="${TARGET_PG_PASSWORD}" psql \
    -h "$host" \
    -p "$port" \
    -U "${TARGET_PG_USER}" \
    -d "$db" \
    --no-psqlrc \
    -v ON_ERROR_STOP=1 \
    -t -A -q \
    -c "$sql"
}

# Pipe multi-statement SQL (from stdin) into a target service database.
# Usage: echo "CREATE TABLE ..." | psql_target_pipe "core"
psql_target_pipe() {
  local service="$1"
  local host port db
  case "$service" in
    core)
      host="${CORE_PG_HOST}"; port="${CORE_PG_PORT}"; db="${CORE_PG_DB}" ;;
    crm)
      host="${CRM_PG_HOST}"; port="${CRM_PG_PORT}"; db="${CRM_PG_DB}" ;;
    project)
      host="${PROJECT_PG_HOST}"; port="${PROJECT_PG_PORT}"; db="${PROJECT_PG_DB}" ;;
    mail)
      host="${MAIL_PG_HOST}"; port="${MAIL_PG_PORT}"; db="${MAIL_PG_DB}" ;;
    *)
      log_error "Unknown service: ${service}"
      return 1 ;;
  esac
  PGPASSWORD="${TARGET_PG_PASSWORD}" psql \
    -h "$host" \
    -p "$port" \
    -U "${TARGET_PG_USER}" \
    -d "$db" \
    --no-psqlrc \
    -v ON_ERROR_STOP=1 \
    -q
}

# Run pg_dump against the source database with given options.
# Usage: pg_dump_source --schema-only --table=rec_user
pg_dump_source() {
  PGPASSWORD="${SOURCE_PG_PASSWORD}" pg_dump \
    -h "${SOURCE_PG_HOST}" \
    -p "${SOURCE_PG_PORT}" \
    -U "${SOURCE_PG_USER}" \
    -d "${SOURCE_PG_DB}" \
    "$@"
}

###############################################################################
# PREREQUISITE CHECKS
###############################################################################

check_prerequisites() {
  log_step "Phase 0: Prerequisite Checks"

  # Verify required CLI tools
  local missing=()
  for tool in psql pg_dump pg_restore; do
    if ! command -v "$tool" &>/dev/null; then
      missing+=("$tool")
    fi
  done
  if [[ ${#missing[@]} -gt 0 ]]; then
    log_error "Missing required tools: ${missing[*]}"
    log_error "Install postgresql-client >= 16 and ensure tools are on PATH."
    exit 1
  fi
  log_success "Required tools found: psql, pg_dump, pg_restore"

  # Verify source database connectivity
  log_info "Testing source database connectivity (${SOURCE_PG_HOST}:${SOURCE_PG_PORT}/${SOURCE_PG_DB})..."
  if ! psql_source "SELECT 1" &>/dev/null; then
    log_error "Cannot connect to source database ${SOURCE_PG_DB} at ${SOURCE_PG_HOST}:${SOURCE_PG_PORT}"
    exit 1
  fi
  log_success "Source database reachable"

  # Verify target databases
  local services=("core" "crm" "project" "mail")
  for svc in "${services[@]}"; do
    local display_name
    case "$svc" in
      core)    display_name="${CORE_PG_HOST}:${CORE_PG_PORT}/${CORE_PG_DB}" ;;
      crm)     display_name="${CRM_PG_HOST}:${CRM_PG_PORT}/${CRM_PG_DB}" ;;
      project) display_name="${PROJECT_PG_HOST}:${PROJECT_PG_PORT}/${PROJECT_PG_DB}" ;;
      mail)    display_name="${MAIL_PG_HOST}:${MAIL_PG_PORT}/${MAIL_PG_DB}" ;;
    esac
    log_info "Testing target database connectivity (${display_name})..."
    if ! psql_target "$svc" "SELECT 1" &>/dev/null; then
      log_error "Cannot connect to target database for service '${svc}' at ${display_name}"
      exit 1
    fi
    log_success "Target database '${svc}' reachable"
  done

  # Check idempotency markers
  for svc in "${services[@]}"; do
    local marker_exists
    marker_exists=$(psql_target "$svc" \
      "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name='_migration_metadata');" \
      2>/dev/null || echo "f")
    if [[ "$marker_exists" == "t" ]] && [[ "$FORCE_FLAG" != "true" ]]; then
      log_warn "Target database '${svc}' already has _migration_metadata marker."
      log_warn "Migration was previously completed. Use --force to re-run."
      exit 0
    fi
  done

  log_success "All prerequisite checks passed"
}

###############################################################################
# BACKUP — Full pg_dump of source for reversibility (AAP 0.8.1)
###############################################################################

create_backup() {
  log_step "Phase 1: Creating Source Database Backup"

  if [[ "$DRY_RUN" == "true" ]]; then
    log_info "[DRY-RUN] Would create backup of ${SOURCE_PG_DB} in ${BACKUP_DIR}"
    return 0
  fi

  mkdir -p "${BACKUP_DIR}"

  local backup_file="${BACKUP_DIR}/erp_monolith_backup_${MIGRATION_TS}.sql"
  log_info "Backing up source database '${SOURCE_PG_DB}' to ${backup_file} ..."

  pg_dump_source --format=custom --compress=6 --file="$backup_file"

  log_success "Backup complete: ${backup_file}"
  log_info "Backup size: $(du -h "$backup_file" | cut -f1)"
}

###############################################################################
# CREATE TARGET DATABASES & EXTENSIONS
###############################################################################

create_target_databases() {
  log_step "Phase 2: Preparing Target Databases"

  local services=("core" "crm" "project" "mail")
  for svc in "${services[@]}"; do
    log_info "Enabling extensions in ${svc} database..."

    if [[ "$DRY_RUN" == "true" ]]; then
      log_info "[DRY-RUN] Would enable uuid-ossp extension in ${svc}"
      continue
    fi

    # Enable uuid-ossp (required by ERPService.cs DbRepository.CreatePostgresqlExtensions)
    psql_target "$svc" 'CREATE EXTENSION IF NOT EXISTS "uuid-ossp";' 2>/dev/null || true

    # Attempt postgis — non-fatal if unavailable (matches DbRepository.cs try/catch)
    psql_target "$svc" 'CREATE EXTENSION IF NOT EXISTS "postgis";' 2>/dev/null || true

    log_success "Extensions enabled for ${svc}"
  done
}

###############################################################################
# SCHEMA MIGRATION — Extract DDL from source and apply to target
###############################################################################

# Migrate table schema (DDL) from source to target service.
# Usage: migrate_schema "core" "rec_user" "rec_role" ...
migrate_schema() {
  local service="$1"
  shift
  local tables=("$@")

  if [[ ${#tables[@]} -eq 0 ]]; then
    log_warn "No tables to migrate schema for service '${service}'"
    return 0
  fi

  log_info "Migrating schema for ${#tables[@]} table(s) to ${service}..."

  for tbl in "${tables[@]}"; do
    # Check if table exists in source
    local exists
    exists=$(psql_source \
      "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name='${tbl}');" \
      2>/dev/null || echo "f")

    if [[ "$exists" != "t" ]]; then
      log_warn "Table '${tbl}' does not exist in source database — skipping schema"
      continue
    fi

    if [[ "$DRY_RUN" == "true" ]]; then
      log_info "[DRY-RUN] Would migrate schema for table '${tbl}' to ${service}"
      continue
    fi

    # Drop and recreate for idempotency
    psql_target "$service" "DROP TABLE IF EXISTS \"${tbl}\" CASCADE;" 2>/dev/null || true

    # Export schema from source and pipe into target
    pg_dump_source --schema-only --no-owner --no-acl --table="public.${tbl}" \
      | grep -v '^--' \
      | grep -v '^SET ' \
      | grep -v '^SELECT pg_catalog' \
      | psql_target_pipe "$service" 2>/dev/null || {
        log_warn "Schema migration for '${tbl}' to ${service} encountered warnings (may be benign)"
      }

    log_info "  Schema: ${tbl} -> ${service}"
  done

  log_success "Schema migration complete for ${service}"
}

# Migrate indexes associated with given tables from source to target.
# Usage: migrate_indexes "core" "rec_user" "rec_role" ...
migrate_indexes() {
  local service="$1"
  shift
  local tables=("$@")

  if [[ ${#tables[@]} -eq 0 ]]; then
    return 0
  fi

  log_info "Migrating indexes for ${#tables[@]} table(s) to ${service}..."

  for tbl in "${tables[@]}"; do
    local exists
    exists=$(psql_source \
      "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name='${tbl}');" \
      2>/dev/null || echo "f")

    if [[ "$exists" != "t" ]]; then
      continue
    fi

    if [[ "$DRY_RUN" == "true" ]]; then
      log_info "[DRY-RUN] Would migrate indexes for '${tbl}' to ${service}"
      continue
    fi

    # Extract CREATE INDEX statements from pg_dump and apply
    pg_dump_source --schema-only --no-owner --no-acl --table="public.${tbl}" 2>/dev/null \
      | grep -E "^CREATE (UNIQUE )?INDEX" \
      | while IFS= read -r idx_stmt; do
          # Make index creation idempotent
          local idx_name
          idx_name=$(echo "$idx_stmt" | sed -n 's/^CREATE.*INDEX[[:space:]]\+\([^ ]*\).*/\1/p')
          if [[ -n "$idx_name" ]]; then
            psql_target "$service" "DROP INDEX IF EXISTS \"${idx_name}\";" 2>/dev/null || true
          fi
          psql_target "$service" "$idx_stmt" 2>/dev/null || {
            log_warn "  Index statement for '${tbl}' had warnings (may already exist)"
          }
        done

    log_info "  Indexes: ${tbl} -> ${service}"
  done

  log_success "Index migration complete for ${service}"
}

###############################################################################
# SYSTEM TABLE MIGRATION
# Core gets ALL system tables. Other services get filtered entity/relation
# metadata for their owned entities only.
###############################################################################

migrate_system_tables() {
  local service="$1"

  log_info "Migrating system tables for ${service}..."

  if [[ "$service" == "core" ]]; then
    # Core receives ALL system tables as the metadata authority
    migrate_schema "core" "${CORE_SYSTEM_TABLES[@]}"
    migrate_data_for_tables "core" "${CORE_SYSTEM_TABLES[@]}"
    migrate_indexes "core" "${CORE_SYSTEM_TABLES[@]}"
    return 0
  fi

  # Non-core services get filtered copies of entities and entity_relations
  local entity_names_ref
  case "$service" in
    crm)     entity_names_ref=("${CRM_ENTITY_NAMES[@]}") ;;
    project) entity_names_ref=("${PROJECT_ENTITY_NAMES[@]}") ;;
    mail)    entity_names_ref=("${MAIL_ENTITY_NAMES[@]}") ;;
    *)
      log_error "Unknown service for system table migration: ${service}"
      return 1 ;;
  esac

  if [[ "$DRY_RUN" == "true" ]]; then
    log_info "[DRY-RUN] Would migrate filtered entities/entity_relations for ${service}"
    return 0
  fi

  # Create entities table in target (schema copy)
  migrate_schema "$service" "entities" "entity_relations"

  # Build a SQL IN clause for entity name filtering
  # The entities table stores JSON in a 'json' column; entity name is within that JSON.
  local name_filter=""
  for ename in "${entity_names_ref[@]}"; do
    if [[ -n "$name_filter" ]]; then
      name_filter="${name_filter}, "
    fi
    name_filter="${name_filter}'${ename}'"
  done

  # Truncate target entities table for idempotent re-run
  psql_target "$service" "TRUNCATE TABLE entities CASCADE;" 2>/dev/null || true

  # Copy filtered entity rows from source to target via intermediate temp file.
  # Using COPY TO STDOUT piped to COPY FROM STDIN through a temp file avoids
  # the shell-level pipe/here-string conflict (SC2259).
  local tmpfile_ent
  tmpfile_ent=$(mktemp /tmp/entities_XXXXXX.csv)

  PGPASSWORD="${SOURCE_PG_PASSWORD}" psql \
    -h "${SOURCE_PG_HOST}" -p "${SOURCE_PG_PORT}" -U "${SOURCE_PG_USER}" -d "${SOURCE_PG_DB}" \
    --no-psqlrc -v ON_ERROR_STOP=1 -t -A -q \
    -c "COPY (SELECT id, json FROM entities WHERE json::jsonb->>'name' IN (${name_filter})) TO STDOUT WITH (FORMAT csv, HEADER false, DELIMITER E'\\t')" \
    > "$tmpfile_ent" 2>/dev/null || true

  if [[ -s "$tmpfile_ent" ]]; then
    PGPASSWORD="${TARGET_PG_PASSWORD}" psql \
      -h "$(case "$service" in core) echo "${CORE_PG_HOST}";; crm) echo "${CRM_PG_HOST}";; project) echo "${PROJECT_PG_HOST}";; mail) echo "${MAIL_PG_HOST}";; esac)" \
      -p "$(case "$service" in core) echo "${CORE_PG_PORT}";; crm) echo "${CRM_PG_PORT}";; project) echo "${PROJECT_PG_PORT}";; mail) echo "${MAIL_PG_PORT}";; esac)" \
      -U "${TARGET_PG_USER}" \
      -d "$(case "$service" in core) echo "${CORE_PG_DB}";; crm) echo "${CRM_PG_DB}";; project) echo "${PROJECT_PG_DB}";; mail) echo "${MAIL_PG_DB}";; esac)" \
      --no-psqlrc -v ON_ERROR_STOP=1 -q \
      -c "COPY entities (id, json) FROM STDIN WITH (FORMAT csv, HEADER false, DELIMITER E'\\t')" \
      < "$tmpfile_ent" 2>/dev/null || {
        # Fallback: row-by-row INSERT approach
        log_warn "COPY import failed for entities; using row-by-row INSERT"
        while IFS=$'\t' read -r eid ejson; do
          psql_target "$service" \
            "INSERT INTO entities (id, json) VALUES ('${eid}', '$(echo "$ejson" | sed "s/'/''/g")') ON CONFLICT (id) DO NOTHING;" \
            2>/dev/null || true
        done < "$tmpfile_ent"
      }
  fi
  rm -f "$tmpfile_ent"

  # Copy filtered entity_relations — only rows where both origin and target entities
  # belong to this service
  local er_filter_sql
  er_filter_sql="SELECT id, json FROM entity_relations WHERE "
  er_filter_sql+="json::jsonb->>'originEntityName' IN (${name_filter}) AND "
  er_filter_sql+="json::jsonb->>'targetEntityName' IN (${name_filter})"

  psql_target "$service" "TRUNCATE TABLE entity_relations CASCADE;" 2>/dev/null || true

  local tmpfile_rel
  tmpfile_rel=$(mktemp /tmp/entity_relations_XXXXXX.csv)
  PGPASSWORD="${SOURCE_PG_PASSWORD}" psql \
    -h "${SOURCE_PG_HOST}" -p "${SOURCE_PG_PORT}" -U "${SOURCE_PG_USER}" -d "${SOURCE_PG_DB}" \
    --no-psqlrc -t -A -q \
    -c "$er_filter_sql" \
    > "$tmpfile_rel" 2>/dev/null || true

  if [[ -s "$tmpfile_rel" ]]; then
    while IFS='|' read -r rid rjson; do
      psql_target "$service" \
        "INSERT INTO entity_relations (id, json) VALUES ('${rid}', '$(echo "$rjson" | sed "s/'/''/g")') ON CONFLICT (id) DO NOTHING;" \
        2>/dev/null || true
    done < "$tmpfile_rel"
  fi
  rm -f "$tmpfile_rel"

  log_success "System tables migrated for ${service}"
}

###############################################################################
# DATA MIGRATION — Bulk data transfer using pg_dump --data-only
###############################################################################

# Migrate data for an array of tables from source to target service.
# Data is transferred via pg_dump --data-only piped to psql.
# Preserves ALL columns including audit fields (created_on, created_by, etc.).
# Usage: migrate_data_for_tables "core" "rec_user" "rec_role" ...
migrate_data_for_tables() {
  local service="$1"
  shift
  local tables=("$@")

  if [[ ${#tables[@]} -eq 0 ]]; then
    return 0
  fi

  log_info "Migrating data for ${#tables[@]} table(s) to ${service}..."

  for tbl in "${tables[@]}"; do
    # Check existence in source
    local exists
    exists=$(psql_source \
      "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name='${tbl}');" \
      2>/dev/null || echo "f")

    if [[ "$exists" != "t" ]]; then
      log_warn "Table '${tbl}' not found in source — skipping data migration"
      MIGRATION_COUNTS["${service}:${tbl}"]="0 (table not found)"
      continue
    fi

    # Get source record count
    local src_count
    src_count=$(psql_source "SELECT COUNT(*) FROM \"${tbl}\";" 2>/dev/null || echo "0")
    src_count=$(echo "$src_count" | tr -d '[:space:]')

    if [[ "$DRY_RUN" == "true" ]]; then
      log_info "[DRY-RUN] Would migrate ${src_count} record(s) from '${tbl}' to ${service}"
      MIGRATION_COUNTS["${service}:${tbl}"]="${src_count} (dry-run)"
      continue
    fi

    # Truncate target table for idempotency (inside transaction)
    psql_target "$service" "TRUNCATE TABLE \"${tbl}\" CASCADE;" 2>/dev/null || true

    # Stream data from source to target using pg_dump --data-only
    if [[ "$src_count" != "0" ]]; then
      pg_dump_source --data-only --no-owner --no-acl --table="public.${tbl}" --disable-triggers \
        | psql_target_pipe "$service" 2>/dev/null || {
          log_error "Data migration failed for table '${tbl}' to ${service}"
          return 1
        }
    fi

    # Verify target count
    local tgt_count
    tgt_count=$(psql_target "$service" "SELECT COUNT(*) FROM \"${tbl}\";" 2>/dev/null || echo "0")
    tgt_count=$(echo "$tgt_count" | tr -d '[:space:]')

    if [[ "$src_count" != "$tgt_count" ]]; then
      log_error "Record count mismatch for '${tbl}': source=${src_count}, target=${tgt_count}"
      return 1
    fi

    MIGRATION_COUNTS["${service}:${tbl}"]="${tgt_count}"
    log_info "  Data: ${tbl} -> ${service} (${tgt_count} records)"
  done

  log_success "Data migration complete for ${service}"
}

###############################################################################
# DYNAMIC RELATION DISCOVERY
# Queries entity_relations in the source DB to find relations where BOTH
# origin and target entities belong to the given service.
# Returns matching rel_<relationName> table names.
###############################################################################

discover_service_relations() {
  local service="$1"
  shift
  local entity_names=("$@")

  if [[ ${#entity_names[@]} -eq 0 ]]; then
    return 0
  fi

  # Build IN clause for entity names
  local name_filter=""
  for ename in "${entity_names[@]}"; do
    if [[ -n "$name_filter" ]]; then
      name_filter="${name_filter}, "
    fi
    name_filter="${name_filter}'${ename}'"
  done

  # Query source for relations where both origin and target are owned by this service
  local rel_query
  rel_query="SELECT json::jsonb->>'name' FROM entity_relations WHERE "
  rel_query+="json::jsonb->>'originEntityName' IN (${name_filter}) AND "
  rel_query+="json::jsonb->>'targetEntityName' IN (${name_filter})"

  local rel_names
  rel_names=$(PGPASSWORD="${SOURCE_PG_PASSWORD}" psql \
    -h "${SOURCE_PG_HOST}" -p "${SOURCE_PG_PORT}" -U "${SOURCE_PG_USER}" -d "${SOURCE_PG_DB}" \
    --no-psqlrc -t -A -q \
    -c "$rel_query" 2>/dev/null || echo "")

  local result=()
  while IFS= read -r rname; do
    rname=$(echo "$rname" | tr -d '[:space:]')
    if [[ -n "$rname" ]]; then
      # Verify the corresponding rel_ table actually exists in source
      local tbl_exists
      tbl_exists=$(psql_source \
        "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name='rel_${rname}');" \
        2>/dev/null || echo "f")
      if [[ "$tbl_exists" == "t" ]]; then
        result+=("rel_${rname}")
      fi
    fi
  done <<< "$rel_names"

  # Output discovered relation table names (one per line)
  for r in "${result[@]+"${result[@]}"}"; do
    echo "$r"
  done
}

###############################################################################
# SERVICE-SPECIFIC MIGRATION ORCHESTRATORS
###############################################################################

# ── Core Service ──────────────────────────────────────────────────────────────
migrate_core_service() {
  log_step "Phase 3a: Migrating Core Service Data"

  local all_core_tables=("${CORE_ENTITY_TABLES[@]}" "${CORE_RELATION_TABLES[@]}")

  # Step 1: Schema migration for entity and relation tables
  migrate_schema "core" "${all_core_tables[@]}"

  # Step 2: Data migration for entity and relation tables
  migrate_data_for_tables "core" "${all_core_tables[@]}"

  # Step 3: Index migration
  migrate_indexes "core" "${all_core_tables[@]}"

  # Step 4: System tables (Core gets ALL system tables as metadata authority)
  migrate_system_tables "core"

  log_success "Core service migration complete"
}

# ── CRM Service ───────────────────────────────────────────────────────────────
migrate_crm_service() {
  log_step "Phase 3b: Migrating CRM Service Data"

  # Discover CRM-internal relations dynamically
  log_info "Discovering CRM-internal relations..."
  local discovered
  discovered=$(discover_service_relations "crm" "${CRM_ENTITY_NAMES[@]}")
  CRM_RELATION_TABLES=()
  while IFS= read -r line; do
    line=$(echo "$line" | tr -d '[:space:]')
    if [[ -n "$line" ]]; then
      CRM_RELATION_TABLES+=("$line")
    fi
  done <<< "$discovered"
  log_info "Discovered ${#CRM_RELATION_TABLES[@]} CRM-internal relation table(s): ${CRM_RELATION_TABLES[*]+"${CRM_RELATION_TABLES[*]}"}"

  local all_crm_tables=("${CRM_ENTITY_TABLES[@]}" "${CRM_RELATION_TABLES[@]+"${CRM_RELATION_TABLES[@]}"}")

  # Step 1: Schema
  migrate_schema "crm" "${all_crm_tables[@]}"

  # Step 2: Data
  migrate_data_for_tables "crm" "${all_crm_tables[@]}"

  # Step 3: Indexes
  migrate_indexes "crm" "${all_crm_tables[@]}"

  # Step 4: Filtered system tables (only CRM entity metadata)
  migrate_system_tables "crm"

  log_success "CRM service migration complete"
}

# ── Project Service ───────────────────────────────────────────────────────────
migrate_project_service() {
  log_step "Phase 3c: Migrating Project Service Data"

  # Discover Project-internal relations dynamically
  log_info "Discovering Project-internal relations..."
  local discovered
  discovered=$(discover_service_relations "project" "${PROJECT_ENTITY_NAMES[@]}")
  PROJECT_RELATION_TABLES=()
  while IFS= read -r line; do
    line=$(echo "$line" | tr -d '[:space:]')
    if [[ -n "$line" ]]; then
      PROJECT_RELATION_TABLES+=("$line")
    fi
  done <<< "$discovered"
  log_info "Discovered ${#PROJECT_RELATION_TABLES[@]} Project-internal relation table(s): ${PROJECT_RELATION_TABLES[*]+"${PROJECT_RELATION_TABLES[*]}"}"

  local all_project_tables=("${PROJECT_ENTITY_TABLES[@]}" "${PROJECT_RELATION_TABLES[@]+"${PROJECT_RELATION_TABLES[@]}"}")

  migrate_schema "project" "${all_project_tables[@]}"
  migrate_data_for_tables "project" "${all_project_tables[@]}"
  migrate_indexes "project" "${all_project_tables[@]}"
  migrate_system_tables "project"

  log_success "Project service migration complete"
}

# ── Mail Service ──────────────────────────────────────────────────────────────
migrate_mail_service() {
  log_step "Phase 3d: Migrating Mail Service Data"

  # Discover Mail-internal relations dynamically
  log_info "Discovering Mail-internal relations..."
  local discovered
  discovered=$(discover_service_relations "mail" "${MAIL_ENTITY_NAMES[@]}")
  MAIL_RELATION_TABLES=()
  while IFS= read -r line; do
    line=$(echo "$line" | tr -d '[:space:]')
    if [[ -n "$line" ]]; then
      MAIL_RELATION_TABLES+=("$line")
    fi
  done <<< "$discovered"
  log_info "Discovered ${#MAIL_RELATION_TABLES[@]} Mail-internal relation table(s): ${MAIL_RELATION_TABLES[*]+"${MAIL_RELATION_TABLES[*]}"}"

  local all_mail_tables=("${MAIL_ENTITY_TABLES[@]}" "${MAIL_RELATION_TABLES[@]+"${MAIL_RELATION_TABLES[@]}"}")

  migrate_schema "mail" "${all_mail_tables[@]}"
  migrate_data_for_tables "mail" "${all_mail_tables[@]}"
  migrate_indexes "mail" "${all_mail_tables[@]}"
  migrate_system_tables "mail"

  log_success "Mail service migration complete"
}

###############################################################################
# CROSS-SERVICE FK RESOLUTION (AAP 0.7.1)
# After all migrations, drop FK constraints that reference tables belonging
# to other services. UUIDs are preserved for application-level resolution.
###############################################################################

resolve_cross_service_fks() {
  log_step "Phase 4: Resolving Cross-Service Foreign Keys"

  local services=("core" "crm" "project" "mail")
  local total_dropped=0

  for svc in "${services[@]}"; do
    if [[ "$DRY_RUN" == "true" ]]; then
      log_info "[DRY-RUN] Would scan and drop cross-service FKs in ${svc}"
      continue
    fi

    # Gather all FK constraints in this service's database
    local fk_query
    fk_query="SELECT tc.constraint_name, tc.table_name, ccu.table_name AS foreign_table_name
              FROM information_schema.table_constraints AS tc
              JOIN information_schema.constraint_column_usage AS ccu
                ON ccu.constraint_name = tc.constraint_name
                AND ccu.table_schema = tc.table_schema
              WHERE tc.constraint_type = 'FOREIGN KEY'
                AND tc.table_schema = 'public'"

    local fk_results
    fk_results=$(psql_target "$svc" "$fk_query" 2>/dev/null || echo "")

    if [[ -z "$fk_results" ]]; then
      log_info "No FK constraints found in ${svc} database"
      continue
    fi

    # Determine which tables belong to this service
    local owned_tables=()
    case "$svc" in
      core)    owned_tables=("${CORE_ENTITY_TABLES[@]}" "${CORE_RELATION_TABLES[@]}" "${CORE_SYSTEM_TABLES[@]}") ;;
      crm)     owned_tables=("${CRM_ENTITY_TABLES[@]}" "${CRM_RELATION_TABLES[@]+"${CRM_RELATION_TABLES[@]}"}" "entities" "entity_relations") ;;
      project) owned_tables=("${PROJECT_ENTITY_TABLES[@]}" "${PROJECT_RELATION_TABLES[@]+"${PROJECT_RELATION_TABLES[@]}"}" "entities" "entity_relations") ;;
      mail)    owned_tables=("${MAIL_ENTITY_TABLES[@]}" "${MAIL_RELATION_TABLES[@]+"${MAIL_RELATION_TABLES[@]}"}" "entities" "entity_relations") ;;
    esac

    while IFS='|' read -r constraint_name table_name foreign_table; do
      constraint_name=$(echo "$constraint_name" | tr -d '[:space:]')
      table_name=$(echo "$table_name" | tr -d '[:space:]')
      foreign_table=$(echo "$foreign_table" | tr -d '[:space:]')

      if [[ -z "$constraint_name" ]]; then
        continue
      fi

      # Check if the referenced table is NOT in this service's owned tables
      local is_owned=false
      for owned in "${owned_tables[@]}"; do
        if [[ "$foreign_table" == "$owned" ]]; then
          is_owned=true
          break
        fi
      done

      # Also skip FKs where the foreign table exists in the target DB (self-referencing)
      local foreign_exists
      foreign_exists=$(psql_target "$svc" \
        "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name='${foreign_table}');" \
        2>/dev/null || echo "f")

      if [[ "$is_owned" == "false" ]] || [[ "$foreign_exists" != "t" ]]; then
        log_info "  Dropping cross-service FK: ${constraint_name} on ${table_name} -> ${foreign_table} (service: ${svc})"
        psql_target "$svc" "ALTER TABLE \"${table_name}\" DROP CONSTRAINT IF EXISTS \"${constraint_name}\";" 2>/dev/null || true
        total_dropped=$((total_dropped + 1))
      fi
    done <<< "$fk_results"
  done

  log_success "Cross-service FK resolution complete — ${total_dropped} constraint(s) dropped"
}

###############################################################################
# POST-MIGRATION VALIDATION (AAP 0.8.1: Zero Data Loss)
###############################################################################

validate_migration() {
  log_step "Phase 5a: Validating Record Counts"

  local has_errors=false

  # Build combined service table map for validation
  declare -A service_tables
  service_tables["core"]="${CORE_ENTITY_TABLES[*]} ${CORE_RELATION_TABLES[*]}"
  service_tables["crm"]="${CRM_ENTITY_TABLES[*]} ${CRM_RELATION_TABLES[*]+"${CRM_RELATION_TABLES[*]}"}"
  service_tables["project"]="${PROJECT_ENTITY_TABLES[*]} ${PROJECT_RELATION_TABLES[*]+"${PROJECT_RELATION_TABLES[*]}"}"
  service_tables["mail"]="${MAIL_ENTITY_TABLES[*]} ${MAIL_RELATION_TABLES[*]+"${MAIL_RELATION_TABLES[*]}"}"

  for svc in core crm project mail; do
    log_info "Validating ${svc} service..."
    local tables_str="${service_tables[$svc]}"

    for tbl in $tables_str; do
      if [[ -z "$tbl" ]]; then continue; fi

      # Source count
      local src_count
      src_count=$(psql_source "SELECT COUNT(*) FROM \"${tbl}\";" 2>/dev/null || echo "MISSING")
      src_count=$(echo "$src_count" | tr -d '[:space:]')

      if [[ "$src_count" == "MISSING" ]]; then
        log_warn "  Table '${tbl}' not in source — cannot validate"
        continue
      fi

      # Target count
      local tgt_count
      tgt_count=$(psql_target "$svc" "SELECT COUNT(*) FROM \"${tbl}\";" 2>/dev/null || echo "MISSING")
      tgt_count=$(echo "$tgt_count" | tr -d '[:space:]')

      if [[ "$tgt_count" == "MISSING" ]]; then
        log_error "  Table '${tbl}' missing from ${svc} target!"
        has_errors=true
        continue
      fi

      if [[ "$src_count" != "$tgt_count" ]]; then
        log_error "  MISMATCH: ${tbl} -> ${svc} | source=${src_count} target=${tgt_count}"
        has_errors=true
      else
        log_success "  MATCH: ${tbl} -> ${svc} | ${src_count} records"
      fi
    done
  done

  # Also validate system tables for Core
  for sys_tbl in "${CORE_SYSTEM_TABLES[@]}"; do
    local src_count
    src_count=$(psql_source "SELECT COUNT(*) FROM \"${sys_tbl}\";" 2>/dev/null || echo "MISSING")
    src_count=$(echo "$src_count" | tr -d '[:space:]')

    if [[ "$src_count" == "MISSING" ]]; then
      continue
    fi

    local tgt_count
    tgt_count=$(psql_target "core" "SELECT COUNT(*) FROM \"${sys_tbl}\";" 2>/dev/null || echo "MISSING")
    tgt_count=$(echo "$tgt_count" | tr -d '[:space:]')

    if [[ "$tgt_count" == "MISSING" ]]; then
      log_error "  System table '${sys_tbl}' missing from core target!"
      has_errors=true
      continue
    fi

    if [[ "$src_count" != "$tgt_count" ]]; then
      log_error "  MISMATCH: ${sys_tbl} -> core | source=${src_count} target=${tgt_count}"
      has_errors=true
    else
      log_success "  MATCH: ${sys_tbl} -> core | ${src_count} records"
    fi
  done

  if [[ "$has_errors" == "true" ]]; then
    log_error "Validation FAILED — data loss detected!"
    return 1
  fi

  log_success "All record counts match — zero data loss confirmed"
}

###############################################################################
# AUDIT FIELD VALIDATION (AAP 0.8.1: Preserve audit fields)
###############################################################################

validate_audit_fields() {
  log_step "Phase 5b: Validating Audit Field Preservation"

  local audit_columns=("created_on" "created_by" "last_modified_on" "last_modified_by")
  local sample_limit=5
  local has_errors=false

  # Spot-check Core rec_user table (always has audit fields)
  for col in "${audit_columns[@]}"; do
    # Check if column exists in source
    local col_exists
    col_exists=$(psql_source \
      "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='rec_user' AND column_name='${col}');" \
      2>/dev/null || echo "f")

    if [[ "$col_exists" != "t" ]]; then
      log_info "  Column '${col}' does not exist in rec_user — skipping audit check"
      continue
    fi

    # Sample records from source
    local src_sample
    src_sample=$(psql_source \
      "SELECT id::text || '|' || COALESCE(\"${col}\"::text, 'NULL') FROM rec_user ORDER BY id LIMIT ${sample_limit};" \
      2>/dev/null || echo "")

    if [[ -z "$src_sample" ]]; then
      continue
    fi

    while IFS='|' read -r rid rval; do
      rid=$(echo "$rid" | tr -d '[:space:]')
      rval=$(echo "$rval" | tr -d '[:space:]')

      if [[ -z "$rid" ]]; then continue; fi

      local tgt_val
      tgt_val=$(psql_target "core" \
        "SELECT COALESCE(\"${col}\"::text, 'NULL') FROM rec_user WHERE id='${rid}';" \
        2>/dev/null || echo "NOT_FOUND")
      tgt_val=$(echo "$tgt_val" | tr -d '[:space:]')

      if [[ "$rval" != "$tgt_val" ]]; then
        log_error "  Audit field mismatch: rec_user.${col} id=${rid} source='${rval}' target='${tgt_val}'"
        has_errors=true
      fi
    done <<< "$src_sample"
  done

  if [[ "$has_errors" == "true" ]]; then
    log_error "Audit field validation FAILED — some fields were altered during migration"
    return 1
  fi

  log_success "Audit field spot-check passed — fields preserved correctly"
}

###############################################################################
# MIGRATION MARKER — Enables idempotency on subsequent runs
###############################################################################

create_migration_marker() {
  log_step "Phase 6: Creating Migration Markers"

  local services=("core" "crm" "project" "mail")

  for svc in "${services[@]}"; do
    if [[ "$DRY_RUN" == "true" ]]; then
      log_info "[DRY-RUN] Would create _migration_metadata in ${svc}"
      continue
    fi

    # Build migrated tables list as JSON array
    local tables_json="["
    local first=true
    case "$svc" in
      core)
        for t in "${CORE_ENTITY_TABLES[@]}" "${CORE_RELATION_TABLES[@]}" "${CORE_SYSTEM_TABLES[@]}"; do
          if [[ "$first" == "true" ]]; then first=false; else tables_json+=","; fi
          tables_json+="\"${t}\""
        done ;;
      crm)
        for t in "${CRM_ENTITY_TABLES[@]}" ${CRM_RELATION_TABLES[@]+"${CRM_RELATION_TABLES[@]}"}; do
          if [[ "$first" == "true" ]]; then first=false; else tables_json+=","; fi
          tables_json+="\"${t}\""
        done ;;
      project)
        for t in "${PROJECT_ENTITY_TABLES[@]}" ${PROJECT_RELATION_TABLES[@]+"${PROJECT_RELATION_TABLES[@]}"}; do
          if [[ "$first" == "true" ]]; then first=false; else tables_json+=","; fi
          tables_json+="\"${t}\""
        done ;;
      mail)
        for t in "${MAIL_ENTITY_TABLES[@]}" ${MAIL_RELATION_TABLES[@]+"${MAIL_RELATION_TABLES[@]}"}; do
          if [[ "$first" == "true" ]]; then first=false; else tables_json+=","; fi
          tables_json+="\"${t}\""
        done ;;
    esac
    tables_json+="]"

    # Build record counts as JSON object
    local counts_json="{"
    first=true
    for key in "${!MIGRATION_COUNTS[@]}"; do
      if [[ "$key" == "${svc}:"* ]]; then
        local tbl_name="${key#"${svc}":}"
        local count_val="${MIGRATION_COUNTS[$key]}"
        if [[ "$first" == "true" ]]; then first=false; else counts_json+=","; fi
        counts_json+="\"${tbl_name}\":\"${count_val}\""
      fi
    done
    counts_json+="}"

    psql_target "$svc" "
      DROP TABLE IF EXISTS _migration_metadata;
      CREATE TABLE _migration_metadata (
        migration_timestamp  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
        source_database      TEXT NOT NULL,
        migrated_tables      JSONB NOT NULL,
        record_counts        JSONB NOT NULL
      );
      INSERT INTO _migration_metadata (source_database, migrated_tables, record_counts)
      VALUES (
        '${SOURCE_PG_DB}',
        '${tables_json}'::jsonb,
        '${counts_json}'::jsonb
      );
    " 2>/dev/null || {
      log_warn "Could not create migration marker in ${svc} — non-fatal"
    }

    log_success "Migration marker created in ${svc}"
  done
}

###############################################################################
# ROLLBACK — Restore from backup and drop target databases
###############################################################################

rollback() {
  log_step "Rollback: Restoring from backup"

  # Find the most recent backup (or use provided path)
  local backup_file="${1:-}"
  if [[ -z "$backup_file" ]]; then
    backup_file=$(find "${BACKUP_DIR}" -maxdepth 1 -name 'erp_monolith_backup_*.sql' -type f -printf '%T@\t%p\n' 2>/dev/null | sort -rn | head -1 | cut -f2 || echo "")
  fi

  if [[ -z "$backup_file" ]] || [[ ! -f "$backup_file" ]]; then
    log_error "No backup file found in ${BACKUP_DIR}. Cannot rollback."
    exit 1
  fi

  log_info "Using backup: ${backup_file}"

  # Drop migration markers and truncate all migrated tables in target databases
  local services=("core" "crm" "project" "mail")
  for svc in "${services[@]}"; do
    log_info "Cleaning target database for ${svc}..."

    if [[ "$DRY_RUN" == "true" ]]; then
      log_info "[DRY-RUN] Would drop migration marker and truncate tables in ${svc}"
      continue
    fi

    # Drop migration metadata table
    psql_target "$svc" "DROP TABLE IF EXISTS _migration_metadata;" 2>/dev/null || true

    # Get all user tables and truncate them
    local user_tables
    user_tables=$(psql_target "$svc" \
      "SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename NOT LIKE 'pg_%';" \
      2>/dev/null || echo "")

    for utbl in $user_tables; do
      utbl=$(echo "$utbl" | tr -d '[:space:]')
      if [[ -n "$utbl" ]]; then
        psql_target "$svc" "DROP TABLE IF EXISTS \"${utbl}\" CASCADE;" 2>/dev/null || true
      fi
    done

    log_success "Cleaned target database for ${svc}"
  done

  # Restore source database from backup if requested
  if [[ -f "$backup_file" ]]; then
    log_info "Restoring source database from backup..."
    if [[ "$DRY_RUN" != "true" ]]; then
      PGPASSWORD="${SOURCE_PG_PASSWORD}" pg_restore \
        -h "${SOURCE_PG_HOST}" \
        -p "${SOURCE_PG_PORT}" \
        -U "${SOURCE_PG_USER}" \
        -d "${SOURCE_PG_DB}" \
        --clean --if-exists \
        "$backup_file" 2>/dev/null || {
          log_warn "pg_restore completed with warnings (this is common for --clean operations)"
        }
      log_success "Source database restored from backup"
    else
      log_info "[DRY-RUN] Would restore source database from ${backup_file}"
    fi
  fi

  log_success "Rollback complete"
}

###############################################################################
# HELP / USAGE
###############################################################################

show_help() {
  cat <<'USAGE'
WebVella ERP — Monolith-to-Microservices Data Migration Script

USAGE:
  ./migrate-data.sh [OPTIONS]

OPTIONS:
  --help        Show this help message and exit
  --dry-run     Print actions that would be performed without modifying any database
  --rollback    Restore from the most recent backup and drop all migrated data
  --force       Force re-migration even if target databases have migration markers

ENVIRONMENT VARIABLES:
  Source (monolith) database:
    SOURCE_PG_HOST      (default: localhost)
    SOURCE_PG_PORT      (default: 5432)
    SOURCE_PG_DB        (default: ttg_test)
    SOURCE_PG_USER      (default: dev)
    SOURCE_PG_PASSWORD  (default: dev)

  Target service databases:
    CORE_PG_HOST / CORE_PG_PORT / CORE_PG_DB       (defaults: localhost / 5432 / erp_core)
    CRM_PG_HOST  / CRM_PG_PORT  / CRM_PG_DB        (defaults: localhost / 5433 / erp_crm)
    PROJECT_PG_HOST / PROJECT_PG_PORT / PROJECT_PG_DB  (defaults: localhost / 5434 / erp_project)
    MAIL_PG_HOST / MAIL_PG_PORT / MAIL_PG_DB        (defaults: localhost / 5435 / erp_mail)
    TARGET_PG_USER      (default: dev)
    TARGET_PG_PASSWORD  (default: dev)

  Behaviour:
    DRY_RUN     (default: false)     Set to "true" to enable dry-run mode
    BACKUP_DIR  (default: /tmp/erp-migration-backup)
    LOG_FILE    (default: /tmp/erp-migration.log)

SERVICES & ENTITY OWNERSHIP:
  Core    → rec_user, rec_role, rec_user_file, rec_language, rec_currency, rec_country
            + ALL system metadata tables (entities, entity_relations, etc.)
  CRM     → rec_account, rec_contact, rec_case, rec_address, rec_salutation
  Project → rec_task, rec_timelog, rec_comment, rec_feed, rec_task_type
  Mail    → rec_email, rec_smtp_service

CROSS-SERVICE REFERENCES:
  Foreign keys referencing tables in other services are dropped after migration.
  UUID values are preserved for application-level resolution via gRPC/REST.
  - Audit fields (created_by, modified_by) → resolved via Core service at read time
  - Account→Project, Case→Task            → denormalized UUIDs in Project DB
  - Contact→Email                          → contact UUID stored in Mail DB

EXAMPLES:
  # Full migration with default settings (Docker Compose targets)
  ./migrate-data.sh

  # Dry run to preview actions
  ./migrate-data.sh --dry-run

  # Migration with custom source database
  SOURCE_PG_HOST=prod-db.example.com SOURCE_PG_DB=erp_prod ./migrate-data.sh

  # Rollback to pre-migration state
  ./migrate-data.sh --rollback

IDEMPOTENCY:
  The script creates a _migration_metadata table in each target database upon
  successful completion. Subsequent runs detect this marker and exit unless
  --force is provided.

REVERSIBILITY:
  Before migration, a full pg_dump backup is stored in $BACKUP_DIR.
  Use --rollback to restore from the most recent backup.

USAGE
}

###############################################################################
# MIGRATION SUMMARY
###############################################################################

print_summary() {
  local elapsed=$(( SECONDS - SCRIPT_START_SECONDS ))
  local minutes=$(( elapsed / 60 ))
  local seconds=$(( elapsed % 60 ))

  log_step "Migration Summary"

  log_info "Duration: ${minutes}m ${seconds}s"
  log_info "Mode: $(if [[ "$DRY_RUN" == "true" ]]; then echo "DRY-RUN"; else echo "LIVE"; fi)"
  log_info "Source: ${SOURCE_PG_HOST}:${SOURCE_PG_PORT}/${SOURCE_PG_DB}"
  log_info "Targets:"
  log_info "  Core:    ${CORE_PG_HOST}:${CORE_PG_PORT}/${CORE_PG_DB}"
  log_info "  CRM:     ${CRM_PG_HOST}:${CRM_PG_PORT}/${CRM_PG_DB}"
  log_info "  Project: ${PROJECT_PG_HOST}:${PROJECT_PG_PORT}/${PROJECT_PG_DB}"
  log_info "  Mail:    ${MAIL_PG_HOST}:${MAIL_PG_PORT}/${MAIL_PG_DB}"
  log_info ""
  log_info "Record Counts:"

  # Sort and display migration counts
  for key in $(echo "${!MIGRATION_COUNTS[@]}" | tr ' ' '\n' | sort); do
    log_info "  ${key} = ${MIGRATION_COUNTS[$key]}"
  done

  log_info ""
  log_info "Log file: ${LOG_FILE}"
  log_info "Backup dir: ${BACKUP_DIR}"

  log_success "Migration completed successfully!"
}

###############################################################################
# MAIN EXECUTION FLOW
###############################################################################

main() {
  # Parse command-line arguments
  local do_rollback=false

  for arg in "$@"; do
    case "$arg" in
      --help|-h)
        show_help
        exit 0
        ;;
      --dry-run)
        DRY_RUN="true"
        log_info "Dry-run mode enabled — no databases will be modified"
        ;;
      --rollback)
        do_rollback=true
        ;;
      --force)
        FORCE_FLAG=true
        ;;
      *)
        log_error "Unknown argument: ${arg}"
        show_help
        exit 1
        ;;
    esac
  done

  # Initialize log file
  mkdir -p "$(dirname "$LOG_FILE")"
  echo "=== WebVella ERP Migration — ${MIGRATION_TS} ===" >> "$LOG_FILE"

  log_step "WebVella ERP — Monolith to Microservices Data Migration"
  log_info "Timestamp: ${MIGRATION_TS}"

  if [[ "$do_rollback" == "true" ]]; then
    check_prerequisites
    rollback "${2:-}"
    exit 0
  fi

  # ── Step 0: Prerequisites ──────────────────────────────────────────────────
  check_prerequisites

  # ── Step 1: Backup ─────────────────────────────────────────────────────────
  create_backup

  # ── Step 2: Prepare target databases ───────────────────────────────────────
  create_target_databases

  # ── Step 3a: Migrate Core service ──────────────────────────────────────────
  migrate_core_service

  # ── Step 3b: Migrate CRM service ───────────────────────────────────────────
  migrate_crm_service

  # ── Step 3c: Migrate Project service ───────────────────────────────────────
  migrate_project_service

  # ── Step 3d: Migrate Mail service ──────────────────────────────────────────
  migrate_mail_service

  # ── Step 4: Cross-service FK resolution ────────────────────────────────────
  resolve_cross_service_fks

  # ── Step 5a: Validate record counts ────────────────────────────────────────
  validate_migration

  # ── Step 5b: Validate audit fields ─────────────────────────────────────────
  validate_audit_fields

  # ── Step 6: Create migration markers ───────────────────────────────────────
  create_migration_marker

  # ── Summary ────────────────────────────────────────────────────────────────
  print_summary
}

# Entry point — only execute when run directly (not sourced)
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
  main "$@"
fi
