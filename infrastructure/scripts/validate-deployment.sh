#!/usr/bin/env bash
##############################################################################
# WebVella ERP Microservices — Deployment Validation Script
#
# Comprehensive validation script that verifies all WebVella ERP
# microservices are healthy and endpoints accessible after deployment.
#
# This script validates:
#   1. Service health endpoints (7 microservices)
#   2. Database connectivity (4 per-service PostgreSQL databases)
#   3. Infrastructure services (Redis, RabbitMQ, optionally LocalStack)
#   4. Gateway routing to backend services
#   5. API v3 backward compatibility (BaseResponseModel envelope format)
#
# Per AAP Sections 0.4.1 (Target Architecture), 0.7.4 (LocalStack
# Validation), 0.8.1 (API Contract Backward Compatibility), and 0.8.3
# (Deployment Validation via LocalStack).
#
# Usage:
#   chmod +x validate-deployment.sh
#   ./validate-deployment.sh
#
# Environment variables override defaults (see Configuration section below).
#
# Required tools:  curl, jq
# Optional tools:  psql (PostgreSQL checks), redis-cli (Redis checks),
#                  aws (LocalStack checks)
#
# Exit codes:
#   0 — All checks passed
#   1 — One or more checks failed
##############################################################################

set -euo pipefail

# =============================================================================
# CONFIGURATION — All values overridable via environment variables
# =============================================================================

# --- Service URLs (from docker-compose.yml, AAP 0.4.1) ---
GATEWAY_URL="${GATEWAY_URL:-http://localhost:5000}"
CORE_URL="${CORE_URL:-http://localhost:5001}"
CRM_URL="${CRM_URL:-http://localhost:5002}"
PROJECT_URL="${PROJECT_URL:-http://localhost:5003}"
MAIL_URL="${MAIL_URL:-http://localhost:5004}"
REPORTING_URL="${REPORTING_URL:-http://localhost:5005}"
ADMIN_URL="${ADMIN_URL:-http://localhost:5006}"

# --- Redis (from docker-compose.yml: redis on port 6379) ---
REDIS_HOST="${REDIS_HOST:-localhost}"
REDIS_PORT="${REDIS_PORT:-6379}"

# --- RabbitMQ (from docker-compose.yml: rabbitmq management port 15672) ---
RABBITMQ_HOST="${RABBITMQ_HOST:-localhost}"
RABBITMQ_MGMT_PORT="${RABBITMQ_MGMT_PORT:-15672}"
RABBITMQ_USER="${RABBITMQ_USER:-guest}"
RABBITMQ_PASSWORD="${RABBITMQ_PASSWORD:-guest}"

# --- LocalStack (from docker-compose.localstack.yml: port 4566, SQS/SNS/S3) ---
LOCALSTACK_URL="${LOCALSTACK_URL:-http://localhost:4566}"
ENABLE_LOCALSTACK_CHECKS="${ENABLE_LOCALSTACK_CHECKS:-false}"

# --- Retry configuration for service health polling ---
MAX_RETRIES="${MAX_RETRIES:-30}"
RETRY_INTERVAL="${RETRY_INTERVAL:-5}"

# --- Per-service PostgreSQL connection details ---
# Matches docker-compose.yml: postgres-core (5432), postgres-crm (5433),
# postgres-project (5434), postgres-mail (5435), credentials dev/dev
PG_CORE_HOST="${PG_CORE_HOST:-localhost}"
PG_CORE_PORT="${PG_CORE_PORT:-5432}"
PG_CORE_DB="${PG_CORE_DB:-erp_core}"

PG_CRM_HOST="${PG_CRM_HOST:-localhost}"
PG_CRM_PORT="${PG_CRM_PORT:-5433}"
PG_CRM_DB="${PG_CRM_DB:-erp_crm}"

PG_PROJECT_HOST="${PG_PROJECT_HOST:-localhost}"
PG_PROJECT_PORT="${PG_PROJECT_PORT:-5434}"
PG_PROJECT_DB="${PG_PROJECT_DB:-erp_project}"

PG_MAIL_HOST="${PG_MAIL_HOST:-localhost}"
PG_MAIL_PORT="${PG_MAIL_PORT:-5435}"
PG_MAIL_DB="${PG_MAIL_DB:-erp_mail}"

PG_USER="${PG_USER:-dev}"
PG_PASSWORD="${PG_PASSWORD:-dev}"

# --- AWS region for LocalStack (from docker-compose.localstack.yml) ---
AWS_REGION="${AWS_REGION:-us-east-1}"

# =============================================================================
# GLOBAL COUNTERS — Track pass/fail for summary
# =============================================================================

PASS_COUNT=0
FAIL_COUNT=0
WARN_COUNT=0
TOTAL_COUNT=0

# =============================================================================
# COLOR-CODED OUTPUT FUNCTIONS
# =============================================================================

# Detect whether stdout supports color output
if [[ -t 1 ]] && command -v tput &>/dev/null && [[ "$(tput colors 2>/dev/null || echo 0)" -ge 8 ]]; then
    COLOR_GREEN="\033[0;32m"
    COLOR_RED="\033[0;31m"
    COLOR_YELLOW="\033[0;33m"
    COLOR_BLUE="\033[0;34m"
    COLOR_BOLD="\033[1m"
    COLOR_RESET="\033[0m"
else
    COLOR_GREEN=""
    COLOR_RED=""
    COLOR_YELLOW=""
    COLOR_BLUE=""
    COLOR_BOLD=""
    COLOR_RESET=""
fi

# log_info — Print an informational message (blue)
log_info() {
    local msg="$1"
    echo -e "${COLOR_BLUE}[INFO]${COLOR_RESET}  $(date '+%Y-%m-%d %H:%M:%S') — ${msg}"
}

# log_success — Print a success message (green) and increment PASS_COUNT
log_success() {
    local msg="$1"
    echo -e "${COLOR_GREEN}[PASS]${COLOR_RESET}  $(date '+%Y-%m-%d %H:%M:%S') — ${msg}"
    (( PASS_COUNT++ )) || true
    (( TOTAL_COUNT++ )) || true
}

# log_error — Print an error message (red) and increment FAIL_COUNT
log_error() {
    local msg="$1"
    echo -e "${COLOR_RED}[FAIL]${COLOR_RESET}  $(date '+%Y-%m-%d %H:%M:%S') — ${msg}"
    (( FAIL_COUNT++ )) || true
    (( TOTAL_COUNT++ )) || true
}

# log_warn — Print a warning message (yellow) and increment WARN_COUNT
log_warn() {
    local msg="$1"
    echo -e "${COLOR_YELLOW}[WARN]${COLOR_RESET}  $(date '+%Y-%m-%d %H:%M:%S') — ${msg}"
    (( WARN_COUNT++ )) || true
}

# print_section — Print a section header with decorative separator
print_section() {
    local title="$1"
    echo ""
    echo -e "${COLOR_BOLD}==========================================================================${COLOR_RESET}"
    echo -e "${COLOR_BOLD}  ${title}${COLOR_RESET}"
    echo -e "${COLOR_BOLD}==========================================================================${COLOR_RESET}"
    echo ""
}

# =============================================================================
# UTILITY FUNCTIONS
# =============================================================================

# check_command — Verifies a required CLI tool is available on the PATH.
# Returns 0 if found, 1 if not. Does NOT record pass/fail (tool availability
# is a prerequisite, not a deployment check).
check_command() {
    local cmd="$1"
    if command -v "${cmd}" &>/dev/null; then
        log_info "Tool '${cmd}' is available."
        return 0
    else
        log_warn "Tool '${cmd}' is NOT available — related checks will be skipped."
        return 1
    fi
}

# wait_for_service — Polls a health endpoint with configurable retries.
# Arguments:
#   $1 — Human-readable service name (e.g. "Core Platform Service")
#   $2 — Health endpoint URL (e.g. "http://localhost:5001/health")
#   $3 — Maximum number of retries (defaults to $MAX_RETRIES)
#   $4 — Seconds between retries (defaults to $RETRY_INTERVAL)
# Returns 0 on success (endpoint responds with HTTP 2xx), 1 on timeout.
wait_for_service() {
    local name="$1"
    local url="$2"
    local max_retries="${3:-$MAX_RETRIES}"
    local interval="${4:-$RETRY_INTERVAL}"
    local attempt=1

    log_info "Waiting for ${name} at ${url} (max ${max_retries} retries, ${interval}s interval)..."

    while [[ ${attempt} -le ${max_retries} ]]; do
        if curl -sf --max-time 5 "${url}" >/dev/null 2>&1; then
            log_success "${name} is healthy (${url}) — responded on attempt ${attempt}."
            return 0
        fi
        if [[ ${attempt} -lt ${max_retries} ]]; then
            sleep "${interval}"
        fi
        (( attempt++ )) || true
    done

    log_error "${name} failed health check after ${max_retries} attempts (${url})."
    return 1
}

# check_http_endpoint — Makes an HTTP request and validates the response
# status code matches the expected value.
# Arguments:
#   $1 — Human-readable check name
#   $2 — Full URL to request
#   $3 — Expected HTTP status code (e.g. "200")
# Records pass/fail.
check_http_endpoint() {
    local name="$1"
    local url="$2"
    local expected_status="$3"
    local actual_status

    actual_status=$(curl -o /dev/null -s -w '%{http_code}' --max-time 10 "${url}" 2>/dev/null) || actual_status="000"

    if [[ "${actual_status}" == "${expected_status}" ]]; then
        log_success "${name}: HTTP ${actual_status} (expected ${expected_status}) — ${url}"
        return 0
    else
        log_error "${name}: HTTP ${actual_status} (expected ${expected_status}) — ${url}"
        return 1
    fi
}

# check_http_json_response — Makes an HTTP request, extracts a JSON field
# using jq, and compares it against an expected value.
# Arguments:
#   $1 — Human-readable check name
#   $2 — Full URL to request
#   $3 — jq path expression (e.g. ".success")
#   $4 — Expected value (string comparison)
# Records pass/fail.
check_http_json_response() {
    local name="$1"
    local url="$2"
    local json_path="$3"
    local expected_value="$4"
    local response
    local actual_value

    # Ensure jq is available for JSON parsing
    if ! command -v jq &>/dev/null; then
        log_warn "${name}: skipped — 'jq' is not installed."
        return 1
    fi

    response=$(curl -s --max-time 10 "${url}" 2>/dev/null) || response=""

    if [[ -z "${response}" ]]; then
        log_error "${name}: empty or no response from ${url}"
        return 1
    fi

    actual_value=$(echo "${response}" | jq -r "${json_path}" 2>/dev/null) || actual_value="<jq_error>"

    if [[ "${actual_value}" == "${expected_value}" ]]; then
        log_success "${name}: ${json_path} == '${expected_value}' — ${url}"
        return 0
    else
        log_error "${name}: ${json_path} == '${actual_value}' (expected '${expected_value}') — ${url}"
        return 1
    fi
}

# check_pg_connection — Verifies PostgreSQL connectivity for a per-service
# database using psql or pg_isready.
# Arguments:
#   $1 — Human-readable database name (e.g. "Core DB (erp_core)")
#   $2 — PostgreSQL host
#   $3 — PostgreSQL port
#   $4 — Database name
#   $5 — Username
#   $6 — Password
# Records pass/fail. Handles graceful degradation if psql is not installed.
check_pg_connection() {
    local name="$1"
    local host="$2"
    local port="$3"
    local db="$4"
    local user="$5"
    local password="$6"

    # Prefer psql for a full connectivity check with SELECT 1
    if command -v psql &>/dev/null; then
        if PGPASSWORD="${password}" psql -h "${host}" -p "${port}" -U "${user}" -d "${db}" -c "SELECT 1" >/dev/null 2>&1; then
            log_success "${name}: PostgreSQL connection OK (${host}:${port}/${db})"
            return 0
        else
            log_error "${name}: PostgreSQL connection FAILED (${host}:${port}/${db})"
            return 1
        fi
    # Fall back to pg_isready which only checks if the server accepts connections
    elif command -v pg_isready &>/dev/null; then
        if pg_isready -h "${host}" -p "${port}" -d "${db}" -U "${user}" >/dev/null 2>&1; then
            log_success "${name}: PostgreSQL is ready (${host}:${port}/${db}) [pg_isready]"
            return 0
        else
            log_error "${name}: PostgreSQL is NOT ready (${host}:${port}/${db}) [pg_isready]"
            return 1
        fi
    else
        log_warn "${name}: skipped — neither 'psql' nor 'pg_isready' is installed."
        return 1
    fi
}

# check_redis — Verifies Redis distributed cache connectivity using
# redis-cli ping, expecting a PONG response.
# Arguments:
#   $1 — Redis host
#   $2 — Redis port
# Records pass/fail. Handles graceful degradation if redis-cli is not installed.
check_redis() {
    local host="$1"
    local port="$2"
    local pong_response

    if ! command -v redis-cli &>/dev/null; then
        log_warn "Redis check: skipped — 'redis-cli' is not installed."
        return 1
    fi

    pong_response=$(redis-cli -h "${host}" -p "${port}" ping 2>/dev/null) || pong_response=""

    if [[ "${pong_response}" == "PONG" ]]; then
        log_success "Redis: PONG received (${host}:${port})"
        return 0
    else
        log_error "Redis: no PONG response (${host}:${port}) — got '${pong_response}'"
        return 1
    fi
}

# check_rabbitmq — Verifies RabbitMQ message broker connectivity using the
# management API health endpoint (/api/health/checks/alarms).
# Arguments:
#   $1 — RabbitMQ host
#   $2 — RabbitMQ management port (default 15672)
# Records pass/fail. Uses curl for HTTP-based health check.
check_rabbitmq() {
    local host="$1"
    local port="$2"
    local mgmt_url="http://${host}:${port}/api/health/checks/alarms"
    local status_code

    status_code=$(curl -o /dev/null -s -w '%{http_code}' --max-time 5 \
        -u "${RABBITMQ_USER}:${RABBITMQ_PASSWORD}" \
        "${mgmt_url}" 2>/dev/null) || status_code="000"

    if [[ "${status_code}" == "200" ]]; then
        log_success "RabbitMQ: management API healthy (${mgmt_url})"
        return 0
    else
        log_error "RabbitMQ: management API returned HTTP ${status_code} (${mgmt_url})"
        return 1
    fi
}

# check_localstack_service — Verifies a specific LocalStack-emulated AWS
# service is operational by executing service-specific commands.
# Arguments:
#   $1 — Service name: "sqs", "sns", or "s3"
# Records pass/fail. Handles graceful degradation if aws CLI is not installed.
check_localstack_service() {
    local service_name="$1"

    if ! command -v aws &>/dev/null; then
        log_warn "LocalStack ${service_name}: skipped — 'aws' CLI is not installed."
        return 1
    fi

    local aws_cmd="aws --endpoint-url=${LOCALSTACK_URL} --region ${AWS_REGION} --no-cli-pager"

    case "${service_name}" in
        sqs)
            if ${aws_cmd} sqs list-queues >/dev/null 2>&1; then
                log_success "LocalStack SQS: service is operational (${LOCALSTACK_URL})"
                return 0
            else
                log_error "LocalStack SQS: service check FAILED (${LOCALSTACK_URL})"
                return 1
            fi
            ;;
        sns)
            if ${aws_cmd} sns list-topics >/dev/null 2>&1; then
                log_success "LocalStack SNS: service is operational (${LOCALSTACK_URL})"
                return 0
            else
                log_error "LocalStack SNS: service check FAILED (${LOCALSTACK_URL})"
                return 1
            fi
            ;;
        s3)
            if ${aws_cmd} s3 ls >/dev/null 2>&1; then
                log_success "LocalStack S3: service is operational (${LOCALSTACK_URL})"
                return 0
            else
                log_error "LocalStack S3: service check FAILED (${LOCALSTACK_URL})"
                return 1
            fi
            ;;
        *)
            log_error "LocalStack: unknown service '${service_name}'"
            return 1
            ;;
    esac
}

# print_summary — Prints final pass/fail/warn counts and exits with the
# appropriate code: 0 if all checks passed, 1 if any check failed.
print_summary() {
    print_section "DEPLOYMENT VALIDATION SUMMARY"

    echo -e "  ${COLOR_GREEN}Passed:${COLOR_RESET}   ${PASS_COUNT}"
    echo -e "  ${COLOR_RED}Failed:${COLOR_RESET}   ${FAIL_COUNT}"
    echo -e "  ${COLOR_YELLOW}Warnings:${COLOR_RESET} ${WARN_COUNT}"
    echo -e "  ${COLOR_BOLD}Total:${COLOR_RESET}    ${TOTAL_COUNT}"
    echo ""

    if [[ ${FAIL_COUNT} -eq 0 ]]; then
        echo -e "${COLOR_GREEN}${COLOR_BOLD}  ✓ All deployment validation checks PASSED.${COLOR_RESET}"
        echo ""
        return 0
    else
        echo -e "${COLOR_RED}${COLOR_BOLD}  ✗ ${FAIL_COUNT} deployment validation check(s) FAILED.${COLOR_RESET}"
        echo ""
        return 1
    fi
}

# =============================================================================
# MAIN EXECUTION
# =============================================================================

main() {
    print_section "WebVella ERP Microservices — Deployment Validation"

    log_info "Validating deployment with the following configuration:"
    log_info "  Gateway URL:    ${GATEWAY_URL}"
    log_info "  Core URL:       ${CORE_URL}"
    log_info "  CRM URL:        ${CRM_URL}"
    log_info "  Project URL:    ${PROJECT_URL}"
    log_info "  Mail URL:       ${MAIL_URL}"
    log_info "  Reporting URL:  ${REPORTING_URL}"
    log_info "  Admin URL:      ${ADMIN_URL}"
    log_info "  Redis:          ${REDIS_HOST}:${REDIS_PORT}"
    log_info "  RabbitMQ Mgmt:  ${RABBITMQ_HOST}:${RABBITMQ_MGMT_PORT}"
    log_info "  LocalStack:     ${LOCALSTACK_URL} (checks enabled: ${ENABLE_LOCALSTACK_CHECKS})"
    log_info "  Max retries:    ${MAX_RETRIES}, interval: ${RETRY_INTERVAL}s"

    # =========================================================================
    # PREREQUISITE CHECKS — Verify required and optional tools
    # =========================================================================

    print_section "Checking Prerequisites"

    local has_jq=false

    # curl is mandatory for all HTTP-based checks
    if ! check_command "curl"; then
        log_error "FATAL: 'curl' is required but not installed. Cannot proceed."
        FAIL_COUNT=1
        TOTAL_COUNT=1
        print_summary
        exit 1
    fi

    # jq is needed for JSON response validation (API v3 backward compat)
    check_command "jq" && has_jq=true

    # psql, redis-cli, aws are optional — functions degrade gracefully if absent
    check_command "psql"      || true
    check_command "redis-cli" || true
    check_command "aws"       || true

    # =========================================================================
    # PHASE 1: SERVICE HEALTH ENDPOINTS
    # =========================================================================

    print_section "Checking Service Health Endpoints"

    # Core Platform Service (AAP 0.4.1: Entity, Record, Security, File, Search, DataSource)
    wait_for_service "Core Platform Service" "${CORE_URL}/health" "${MAX_RETRIES}" "${RETRY_INTERVAL}" || true

    # CRM Service (AAP 0.4.1: Account, Contact, Case, Address, Salutation)
    wait_for_service "CRM Service" "${CRM_URL}/health" "${MAX_RETRIES}" "${RETRY_INTERVAL}" || true

    # Project/Task Service (AAP 0.4.1: Task, Timelog, Comment, Feed)
    wait_for_service "Project/Task Service" "${PROJECT_URL}/health" "${MAX_RETRIES}" "${RETRY_INTERVAL}" || true

    # Mail/Notification Service (AAP 0.4.1: Email, SMTP, Queue Processing)
    wait_for_service "Mail/Notification Service" "${MAIL_URL}/health" "${MAX_RETRIES}" "${RETRY_INTERVAL}" || true

    # Reporting Service (AAP 0.4.1: Aggregation, event-sourced projections)
    wait_for_service "Reporting Service" "${REPORTING_URL}/health" "${MAX_RETRIES}" "${RETRY_INTERVAL}" || true

    # Admin/SDK Service (AAP 0.4.1: Code generation, log management, entity designer)
    wait_for_service "Admin/SDK Service" "${ADMIN_URL}/health" "${MAX_RETRIES}" "${RETRY_INTERVAL}" || true

    # API Gateway / BFF (AAP 0.4.1: Single entry point on port 5000)
    wait_for_service "API Gateway" "${GATEWAY_URL}/health" "${MAX_RETRIES}" "${RETRY_INTERVAL}" || true

    # =========================================================================
    # PHASE 2: DATABASE CONNECTIVITY
    # Matches docker-compose.yml: per-service PostgreSQL databases
    # Connection format from Config.json / AAP:
    #   Server=<host>;Port=5432;User Id=dev;Password=dev;Database=<db>;
    #   Pooling=true;MinPoolSize=1;MaxPoolSize=100;CommandTimeout=120;
    # =========================================================================

    print_section "Checking Database Connectivity"

    # Core database (postgres-core: erp_core on port 5432)
    check_pg_connection "Core DB (erp_core)" \
        "${PG_CORE_HOST}" "${PG_CORE_PORT}" "${PG_CORE_DB}" \
        "${PG_USER}" "${PG_PASSWORD}" || true

    # CRM database (postgres-crm: erp_crm on port 5433)
    check_pg_connection "CRM DB (erp_crm)" \
        "${PG_CRM_HOST}" "${PG_CRM_PORT}" "${PG_CRM_DB}" \
        "${PG_USER}" "${PG_PASSWORD}" || true

    # Project database (postgres-project: erp_project on port 5434)
    check_pg_connection "Project DB (erp_project)" \
        "${PG_PROJECT_HOST}" "${PG_PROJECT_PORT}" "${PG_PROJECT_DB}" \
        "${PG_USER}" "${PG_PASSWORD}" || true

    # Mail database (postgres-mail: erp_mail on port 5435)
    check_pg_connection "Mail DB (erp_mail)" \
        "${PG_MAIL_HOST}" "${PG_MAIL_PORT}" "${PG_MAIL_DB}" \
        "${PG_USER}" "${PG_PASSWORD}" || true

    # =========================================================================
    # PHASE 3: INFRASTRUCTURE SERVICES
    # =========================================================================

    print_section "Checking Infrastructure Services"

    # Redis distributed cache (docker-compose.yml: redis on port 6379)
    check_redis "${REDIS_HOST}" "${REDIS_PORT}" || true

    # RabbitMQ message broker (docker-compose.yml: rabbitmq management port 15672)
    check_rabbitmq "${RABBITMQ_HOST}" "${RABBITMQ_MGMT_PORT}" || true

    # LocalStack (docker-compose.localstack.yml: port 4566, services: SQS, SNS, S3)
    if [[ "${ENABLE_LOCALSTACK_CHECKS}" == "true" ]]; then
        log_info "LocalStack checks are enabled — verifying cloud-native services..."

        # Verify LocalStack health endpoint first
        check_http_endpoint "LocalStack Health" \
            "${LOCALSTACK_URL}/_localstack/health" "200" || true

        # Verify individual AWS services are operational
        check_localstack_service "sqs" || true
        check_localstack_service "sns" || true
        check_localstack_service "s3"  || true
    else
        log_info "LocalStack checks are DISABLED (set ENABLE_LOCALSTACK_CHECKS=true to enable)."
    fi

    # =========================================================================
    # PHASE 4: GATEWAY ROUTING VALIDATION
    # Verify the API Gateway correctly routes requests to backend services.
    # AAP 0.8.1: All existing REST API v3 endpoints must remain accessible
    # through the API Gateway.
    # =========================================================================

    print_section "Checking Gateway Routing"

    # Entity metadata endpoint — should route to Core service
    check_http_endpoint "Gateway → Core (entity list)" \
        "${GATEWAY_URL}/api/v3/en_US/meta/entity/list" "200" || true

    # Record listing endpoint — should route to Core service
    check_http_endpoint "Gateway → Core (user record list)" \
        "${GATEWAY_URL}/api/v3/en_US/record/user/list" "200" || true

    # =========================================================================
    # PHASE 5: API v3 BACKWARD COMPATIBILITY
    # AAP 0.8.1: Response shapes must not change. The BaseResponseModel
    # envelope requires: success, errors, timestamp, message, object fields.
    # =========================================================================

    print_section "Checking API v3 Backward Compatibility"

    local api_v3_entity_url="${GATEWAY_URL}/api/v3/en_US/meta/entity/list"
    local api_v3_record_url="${GATEWAY_URL}/api/v3/en_US/record/user/list"

    if [[ "${has_jq}" == "true" ]]; then
        # --- Entity list endpoint: verify BaseResponseModel envelope ---

        # Verify "success" field exists (boolean)
        check_http_json_response \
            "API v3 entity list — 'success' field exists" \
            "${api_v3_entity_url}" \
            'has("success")' \
            "true" || true

        # Verify "timestamp" field exists
        check_http_json_response \
            "API v3 entity list — 'timestamp' field exists" \
            "${api_v3_entity_url}" \
            'has("timestamp")' \
            "true" || true

        # Verify "errors" field exists (array)
        check_http_json_response \
            "API v3 entity list — 'errors' field exists" \
            "${api_v3_entity_url}" \
            'has("errors")' \
            "true" || true

        # Verify "message" field exists (string)
        check_http_json_response \
            "API v3 entity list — 'message' field exists" \
            "${api_v3_entity_url}" \
            'has("message")' \
            "true" || true

        # Verify "object" field exists
        check_http_json_response \
            "API v3 entity list — 'object' field exists" \
            "${api_v3_entity_url}" \
            'has("object")' \
            "true" || true

        # --- Record list endpoint: verify BaseResponseModel envelope ---

        # Verify "success" field exists (boolean)
        check_http_json_response \
            "API v3 record list — 'success' field exists" \
            "${api_v3_record_url}" \
            'has("success")' \
            "true" || true

        # Verify "timestamp" field exists
        check_http_json_response \
            "API v3 record list — 'timestamp' field exists" \
            "${api_v3_record_url}" \
            'has("timestamp")' \
            "true" || true

        # Verify "errors" field exists (array)
        check_http_json_response \
            "API v3 record list — 'errors' field exists" \
            "${api_v3_record_url}" \
            'has("errors")' \
            "true" || true

        # Verify "message" field exists (string)
        check_http_json_response \
            "API v3 record list — 'message' field exists" \
            "${api_v3_record_url}" \
            'has("message")' \
            "true" || true

        # Verify "object" field exists
        check_http_json_response \
            "API v3 record list — 'object' field exists" \
            "${api_v3_record_url}" \
            'has("object")' \
            "true" || true
    else
        log_warn "API v3 backward compatibility checks skipped — 'jq' is not installed."
    fi

    # =========================================================================
    # SUMMARY AND EXIT
    # =========================================================================

    print_summary
    local exit_code=$?

    exit "${exit_code}"
}

# Invoke main function
main "$@"
