#!/usr/bin/env bash
# =============================================================================
# WebVella ERP — LocalStack CDK Bootstrap & Deploy Script
# =============================================================================
# Bootstraps and deploys ALL CDK stacks to LocalStack, setting up the full
# local AWS environment for the WebVella ERP microservices platform.
#
# This script is the **foundational infrastructure provisioner** — it must
# be run before `seed-test-data.sh` or `run-migrations.sh` because it
# creates all the AWS resources (Lambda, API Gateway, DynamoDB, S3, SQS,
# SNS, Cognito, SSM, RDS, Step Functions) that the other scripts depend on.
#
# Replaces the monolith's manual bootstrap process:
#   - Config.json           → SSM Parameter Store + per-service DynamoDB/RDS
#   - Startup.cs            → CDK stack definitions (13 stacks)
#     ├─ services.AddErp()  → 10 bounded-context service stacks
#     ├─ AddAuthentication() → SharedStack (Cognito) + IdentityStack
#     ├─ UseErpPlugin<SDK>() → PluginSystemStack
#     └─ UseJwtMiddleware()  → ApiGatewayStack JWT authorizer
#   - ERPService.cs         → CDK resource provisioning via cdklocal
#
# CDK Stacks Deployed (13 total):
#   1. SharedStack           — Cognito user pool, SNS event bus, SSM params
#   2. IdentityStack         — Auth Lambdas, user/role DynamoDB
#   3. EntityManagementStack — Entity/field/relation/record CRUD
#   4. CrmStack              — Accounts, contacts, addresses
#   5. InventoryStack        — Tasks, timelogs, products
#   6. InvoicingStack        — Invoices, payments (RDS PostgreSQL)
#   7. ReportingStack        — Analytics, read models (RDS PostgreSQL)
#   8. NotificationsStack    — Email, webhooks, SQS queues
#   9. FileManagementStack   — S3 file storage
#  10. WorkflowStack         — Step Functions orchestration
#  11. PluginSystemStack     — Plugin registry
#  12. ApiGatewayStack       — HTTP API Gateway v2 route-to-Lambda mapping
#  13. FrontendStack         — S3 static SPA hosting
#
# Prerequisites:
#   - Docker daemon running
#   - Node.js 22 LTS installed
#   - cdklocal CLI (npm install -g aws-cdk-local aws-cdk)
#   - awslocal CLI (pip install awscli-local)
#   - curl available
#
# Usage:
#   ./tools/scripts/bootstrap-localstack.sh
#
# Idempotency:
#   Safe to re-run. cdklocal deploy updates existing stacks without
#   duplicating resources. CDK bootstrap is also idempotent.
#
# Environment variables (all have sensible defaults for LocalStack):
#   AWS_ENDPOINT_URL      — LocalStack endpoint (default: http://localhost:4566)
#   AWS_REGION            — AWS region (default: us-east-1)
#   AWS_ACCESS_KEY_ID     — AWS access key (default: test)
#   AWS_SECRET_ACCESS_KEY — AWS secret key (default: test)
#
# References:
#   AAP §0.4.1  — Target: bootstrap-localstack.sh (cdklocal bootstrap + deploy)
#   AAP §0.7.6  — Dual-target CDK: --context localstack=true
#   AAP §0.8.1  — LocalStack is a Docker image pull, NEVER a repository clone
#   AAP §0.8.6  — AWS_ENDPOINT_URL=http://localhost:4566, AWS_REGION=us-east-1
# =============================================================================

set -euo pipefail

# =============================================================================
# Environment Configuration (AAP §0.8.6)
# =============================================================================
# Default values target LocalStack's local endpoint. These match the
# docker-compose.yml localstack service configuration
# (AWS_DEFAULT_REGION=us-east-1) and the CDK app entry point
# (infra/src/app.ts account '000000000000' for LocalStack).
#
# Only test/test credentials are used — LocalStack does not validate them.
# No real AWS credentials are ever required for local development.
# =============================================================================

export AWS_ENDPOINT_URL="${AWS_ENDPOINT_URL:-http://localhost:4566}"
export AWS_REGION="${AWS_REGION:-us-east-1}"
export AWS_DEFAULT_REGION="${AWS_REGION}"
export AWS_ACCESS_KEY_ID="${AWS_ACCESS_KEY_ID:-test}"
export AWS_SECRET_ACCESS_KEY="${AWS_SECRET_ACCESS_KEY:-test}"

# =============================================================================
# Script Variables
# =============================================================================
# Resolve paths relative to the script location so the script works
# regardless of the caller's current working directory.
# =============================================================================

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

# LocalStack health endpoint used for readiness polling
LOCALSTACK_HEALTH_URL="${AWS_ENDPOINT_URL}/_localstack/health"

# Maximum number of retries when waiting for LocalStack to become healthy
HEALTH_CHECK_RETRIES=30

# Seconds between health check retries
HEALTH_CHECK_INTERVAL=2

# =============================================================================
# Helper Functions
# =============================================================================

# Print a formatted info message with timestamp
log_info() {
  echo "[$(date '+%H:%M:%S')] ℹ️  $*"
}

# Print a formatted success message
log_success() {
  echo "[$(date '+%H:%M:%S')] ✅ $*"
}

# Print a formatted warning message
log_warn() {
  echo "[$(date '+%H:%M:%S')] ⚠️  $*"
}

# Print a formatted error message and exit
log_error() {
  echo "[$(date '+%H:%M:%S')] ❌ $*" >&2
  exit 1
}

# Print a section header
print_section() {
  echo ""
  echo "================================================"
  echo "  $*"
  echo "================================================"
}

# Resolve cdklocal command — prefer global install, fall back to npx
resolve_cdklocal() {
  if command -v cdklocal &> /dev/null; then
    echo "cdklocal"
  elif npx cdklocal --version &> /dev/null; then
    echo "npx cdklocal"
  else
    log_error "cdklocal is not installed. Install it with: npm install -g aws-cdk-local aws-cdk"
  fi
}

# Resolve awslocal command — prefer global install, fall back to npx
resolve_awslocal() {
  if command -v awslocal &> /dev/null; then
    echo "awslocal"
  else
    log_error "awslocal is not installed. Install it with: pip install awscli-local"
  fi
}

# =============================================================================
# Phase 1: Prerequisites Validation
# =============================================================================
# Validates that all required tools are installed and accessible before
# attempting any infrastructure operations. This prevents partial/failed
# deployments that leave resources in an inconsistent state.
# =============================================================================

print_section "Phase 0: Validating Prerequisites"

# ---------------------------------------------------------------------------
# Check Docker
# ---------------------------------------------------------------------------
# Docker is required to run the LocalStack container defined in
# docker-compose.yml (services.localstack image: localstack/localstack-pro:latest)
# and for Lambda container execution (LAMBDA_EXECUTOR=docker-reuse).
# ---------------------------------------------------------------------------

if ! command -v docker &> /dev/null; then
  log_error "Docker is not installed. Please install Docker first."
fi

if ! docker info &> /dev/null 2>&1; then
  log_error "Docker daemon is not running. Please start Docker."
fi
log_success "Docker daemon is running"

# ---------------------------------------------------------------------------
# Check Node.js
# ---------------------------------------------------------------------------
# Node.js 22 LTS is required for CDK TypeScript compilation via ts-node.
# The CDK app entry point (infra/cdk.json → "app": "npx ts-node --prefer-ts-exts
# infra/src/app.ts") requires Node.js to synthesize CloudFormation templates.
# ---------------------------------------------------------------------------

if ! command -v node &> /dev/null; then
  log_error "Node.js is not installed. Please install Node.js 22 LTS."
fi
log_success "Node.js found: $(node --version)"

# ---------------------------------------------------------------------------
# Check curl
# ---------------------------------------------------------------------------
# curl is used to poll the LocalStack health endpoint with retry logic
# to ensure LocalStack is fully ready before CDK operations begin.
# ---------------------------------------------------------------------------

if ! command -v curl &> /dev/null; then
  log_error "curl is not installed. Please install curl."
fi
log_success "curl is available"

# ---------------------------------------------------------------------------
# Check cdklocal CLI
# ---------------------------------------------------------------------------
# cdklocal (aws-cdk-local v3.x) is the LocalStack CDK CLI wrapper that
# redirects all CDK CloudFormation API calls to the LocalStack endpoint.
# It enables dual-target CDK deployment (AAP §0.7.6):
#   cdklocal deploy --context localstack=true → LocalStack
#   cdk deploy                                → Production AWS
# ---------------------------------------------------------------------------

CDKLOCAL_CMD=$(resolve_cdklocal)
log_success "cdklocal CLI found: $($CDKLOCAL_CMD --version 2>&1 || echo 'version check skipped')"

# ---------------------------------------------------------------------------
# Check awslocal CLI
# ---------------------------------------------------------------------------
# awslocal (awscli-local) wraps the AWS CLI to automatically point all
# commands at the LocalStack endpoint. Used for post-deployment verification
# of API Gateway, Cognito, DynamoDB, S3, SNS, SQS, and Lambda resources.
# ---------------------------------------------------------------------------

AWSLOCAL_CMD=$(resolve_awslocal)
log_success "awslocal CLI found"

# =============================================================================
# Phase 1: LocalStack Container Management
# =============================================================================
# Ensures the LocalStack container (from docker-compose.yml) is running
# and healthy. If the container is not running, starts it and waits for
# the health endpoint to respond.
#
# The docker-compose.yml defines:
#   services.localstack: container webvella-localstack on port 4566
#   services.stepfunctions-local: container webvella-stepfunctions on port 8083
# =============================================================================

print_section "Phase 1: Ensuring LocalStack is Running"

cd "$REPO_ROOT"

# Check if LocalStack container is running by inspecting docker compose state.
# We look for the localstack service defined in docker-compose.yml.
LOCALSTACK_RUNNING=false
if docker compose ps --format json 2>/dev/null | grep -q "localstack"; then
  LOCALSTACK_RUNNING=true
fi

# Also check using the container name for robustness
if [ "$LOCALSTACK_RUNNING" = false ]; then
  if docker ps --format '{{.Names}}' 2>/dev/null | grep -q "webvella-localstack"; then
    LOCALSTACK_RUNNING=true
  fi
fi

if [ "$LOCALSTACK_RUNNING" = false ]; then
  log_warn "LocalStack container is not running. Starting it now..."
  docker compose up -d

  log_info "Waiting for LocalStack to be ready..."
  RETRIES=$HEALTH_CHECK_RETRIES
  until curl -sf "$LOCALSTACK_HEALTH_URL" > /dev/null 2>&1; do
    RETRIES=$((RETRIES - 1))
    if [ $RETRIES -le 0 ]; then
      log_error "LocalStack failed to start within $((HEALTH_CHECK_RETRIES * HEALTH_CHECK_INTERVAL)) seconds. Check 'docker compose logs localstack' for details."
    fi
    echo "  Waiting for LocalStack... ($RETRIES retries remaining)"
    sleep $HEALTH_CHECK_INTERVAL
  done
  log_success "LocalStack is ready"
else
  log_success "LocalStack container is already running"

  # Even if the container is running, verify it's healthy
  log_info "Verifying LocalStack health endpoint..."
  RETRIES=10
  until curl -sf "$LOCALSTACK_HEALTH_URL" > /dev/null 2>&1; do
    RETRIES=$((RETRIES - 1))
    if [ $RETRIES -le 0 ]; then
      log_error "LocalStack is running but health endpoint is not responding at $LOCALSTACK_HEALTH_URL"
    fi
    echo "  Waiting for health endpoint... ($RETRIES retries remaining)"
    sleep $HEALTH_CHECK_INTERVAL
  done
  log_success "LocalStack health endpoint is responding"
fi

# =============================================================================
# Phase 2: Install npm Dependencies
# =============================================================================
# Ensures all Node.js dependencies are installed before CDK operations.
# The CDK app (infra/src/app.ts) requires aws-cdk-lib, constructs, ts-node,
# and all shared library dependencies to synthesize CloudFormation templates.
#
# npm install is idempotent — safe to run even if dependencies exist.
# =============================================================================

print_section "Phase 2: Ensuring npm Dependencies"

cd "$REPO_ROOT"

if [ ! -d "$REPO_ROOT/node_modules" ]; then
  log_info "node_modules not found. Installing npm dependencies..."
  npm install
  log_success "npm dependencies installed"
else
  log_success "npm dependencies already installed"
fi

# =============================================================================
# Phase 3: CDK Bootstrap
# =============================================================================
# Creates the CDKToolkit CloudFormation stack in LocalStack. This stack
# provisions the S3 staging bucket and IAM roles required before any
# CDK deployment can proceed.
#
# Key flags:
#   --context localstack=true
#     Triggers conditional resource selection in all CDK stacks per
#     AAP §0.7.6 (RDS instead of Aurora, skip CloudFront/ACM/Route53,
#     S3 website hosting instead of S3+CloudFront, correlation-ID
#     structured logging instead of X-Ray).
#
#   aws://000000000000/$AWS_REGION
#     LocalStack's fixed mock account ID. This matches the CDK app
#     entry point (infra/src/app.ts) which sets account to '000000000000'
#     when isLocalStack is true.
#
# cdklocal bootstrap is idempotent — safe to run multiple times.
# =============================================================================

print_section "Phase 3: Bootstrapping CDK Toolkit in LocalStack"

cd "$REPO_ROOT"

log_info "Running cdklocal bootstrap for account 000000000000 in region $AWS_REGION..."

$CDKLOCAL_CMD bootstrap \
  --context localstack=true \
  "aws://000000000000/$AWS_REGION"

log_success "CDK toolkit bootstrap complete"

# =============================================================================
# Phase 4: CDK Deploy All Stacks
# =============================================================================
# Deploys all 13 CDK stacks defined in infra/src/app.ts to LocalStack.
# CDK resolves cross-stack references and deploys in dependency order:
#
#   SharedStack (root)
#     ├── IdentityStack
#     ├── EntityManagementStack
#     ├── CrmStack
#     ├── InventoryStack
#     ├── InvoicingStack
#     ├── ReportingStack
#     ├── NotificationsStack
#     ├── FileManagementStack
#     ├── WorkflowStack
#     └── PluginSystemStack
#           └── ApiGatewayStack
#                 └── FrontendStack
#
# Deployment flags:
#   --all                      Deploy all stacks in dependency order
#   --context localstack=true  Enable LocalStack-specific conditionals:
#                                - RDS PostgreSQL (not Aurora Serverless v2)
#                                - Skip CloudFront CDN, ACM, Route 53
#                                - S3 website hosting for frontend
#                                - Structured logging (not X-Ray)
#   --require-approval never   Auto-approve IAM changes (safe for local dev)
#   --outputs-file             Save stack outputs (API GW URL, Cognito IDs,
#                              DynamoDB table names, etc.) for sibling scripts
#
# cdklocal deploy is idempotent — updates existing stacks on re-run.
# =============================================================================

print_section "Phase 4: Deploying All CDK Stacks to LocalStack"

cd "$REPO_ROOT"

log_info "Deploying all 13 CDK stacks (this may take a few minutes)..."

$CDKLOCAL_CMD deploy --all \
  --context localstack=true \
  --require-approval never \
  --outputs-file "$REPO_ROOT/cdk-outputs.json"

log_success "All CDK stacks deployed successfully"

# =============================================================================
# Phase 5: Post-Deployment Verification
# =============================================================================
# Verifies that all expected AWS resources were created by the CDK deployment.
# Uses awslocal CLI to query each resource type and report counts.
#
# This replaces the monolith's startup-time health checks that were
# implicit in the ASP.NET Core DI container resolution and middleware
# pipeline initialization.
#
# Resource types verified:
#   - HTTP API Gateway (ApiGatewayStack) — route-to-Lambda mapping
#   - Cognito User Pool (SharedStack) — authentication
#   - DynamoDB Tables (per-service stacks) — primary datastores
#   - S3 Buckets (FileManagementStack, FrontendStack) — file + SPA storage
#   - SNS Topics (SharedStack) — domain event bus
#   - SQS Queues (NotificationsStack, per-service DLQs) — message queues
#   - Lambda Functions (all service stacks) — compute handlers
# =============================================================================

print_section "Phase 5: Verifying Deployed Resources"

VERIFICATION_PASSED=true

# ---------------------------------------------------------------------------
# Verify HTTP API Gateway
# ---------------------------------------------------------------------------
echo -n "  HTTP API Gateway:   "
API_RESULT=$($AWSLOCAL_CMD apigatewayv2 get-apis --query 'Items[0].ApiId' --output text 2>/dev/null || echo "NONE")
if [ "$API_RESULT" != "NONE" ] && [ "$API_RESULT" != "None" ] && [ -n "$API_RESULT" ]; then
  echo "$API_RESULT ✅"
else
  echo "NOT FOUND ❌"
  VERIFICATION_PASSED=false
fi

# ---------------------------------------------------------------------------
# Verify Cognito User Pool
# ---------------------------------------------------------------------------
echo -n "  Cognito User Pool:  "
POOL_RESULT=$($AWSLOCAL_CMD cognito-idp list-user-pools --max-results 1 --query 'UserPools[0].Id' --output text 2>/dev/null || echo "NONE")
if [ "$POOL_RESULT" != "NONE" ] && [ "$POOL_RESULT" != "None" ] && [ -n "$POOL_RESULT" ]; then
  echo "$POOL_RESULT ✅"
else
  echo "NOT FOUND ❌"
  VERIFICATION_PASSED=false
fi

# ---------------------------------------------------------------------------
# Verify DynamoDB Tables
# ---------------------------------------------------------------------------
echo -n "  DynamoDB Tables:    "
TABLE_COUNT=$($AWSLOCAL_CMD dynamodb list-tables --query 'TableNames | length(@)' --output text 2>/dev/null || echo "0")
if [ "$TABLE_COUNT" -gt 0 ] 2>/dev/null; then
  echo "$TABLE_COUNT tables ✅"
else
  echo "0 tables ❌"
  VERIFICATION_PASSED=false
fi

# ---------------------------------------------------------------------------
# Verify S3 Buckets
# ---------------------------------------------------------------------------
echo -n "  S3 Buckets:         "
BUCKET_COUNT=$($AWSLOCAL_CMD s3api list-buckets --query 'Buckets | length(@)' --output text 2>/dev/null || echo "0")
if [ "$BUCKET_COUNT" -gt 0 ] 2>/dev/null; then
  echo "$BUCKET_COUNT buckets ✅"
else
  echo "0 buckets ❌"
  VERIFICATION_PASSED=false
fi

# ---------------------------------------------------------------------------
# Verify SNS Topics
# ---------------------------------------------------------------------------
echo -n "  SNS Topics:         "
TOPIC_COUNT=$($AWSLOCAL_CMD sns list-topics --query 'Topics | length(@)' --output text 2>/dev/null || echo "0")
if [ "$TOPIC_COUNT" -gt 0 ] 2>/dev/null; then
  echo "$TOPIC_COUNT topics ✅"
else
  echo "0 topics ⚠️  (may be created lazily)"
fi

# ---------------------------------------------------------------------------
# Verify SQS Queues
# ---------------------------------------------------------------------------
echo -n "  SQS Queues:         "
QUEUE_COUNT=$($AWSLOCAL_CMD sqs list-queues --query 'QueueUrls | length(@)' --output text 2>/dev/null || echo "0")
if [ "$QUEUE_COUNT" != "None" ] && [ "$QUEUE_COUNT" -gt 0 ] 2>/dev/null; then
  echo "$QUEUE_COUNT queues ✅"
else
  echo "0 queues ⚠️  (may be created lazily)"
fi

# ---------------------------------------------------------------------------
# Verify Lambda Functions
# ---------------------------------------------------------------------------
echo -n "  Lambda Functions:   "
LAMBDA_COUNT=$($AWSLOCAL_CMD lambda list-functions --query 'Functions | length(@)' --output text 2>/dev/null || echo "0")
if [ "$LAMBDA_COUNT" -gt 0 ] 2>/dev/null; then
  echo "$LAMBDA_COUNT functions ✅"
else
  echo "0 functions ❌"
  VERIFICATION_PASSED=false
fi

# =============================================================================
# Phase 6: Display Key Endpoints & CDK Outputs
# =============================================================================
# Prints the key endpoints and resource identifiers that developers need
# for local development. These are also persisted in cdk-outputs.json
# for programmatic consumption by sibling scripts.
# =============================================================================

print_section "Phase 6: Key Endpoints & Outputs"

# API Gateway URL
API_ID=$($AWSLOCAL_CMD apigatewayv2 get-apis --query 'Items[0].ApiId' --output text 2>/dev/null || echo "NOT_FOUND")
if [ "$API_ID" != "NOT_FOUND" ] && [ "$API_ID" != "None" ] && [ -n "$API_ID" ]; then
  echo "  API Gateway URL:     ${AWS_ENDPOINT_URL}/restapis/${API_ID}/prod/_user_request_/"
else
  echo "  API Gateway URL:     (not available — deployment may have skipped API Gateway)"
fi

# Cognito User Pool ID
POOL_ID=$($AWSLOCAL_CMD cognito-idp list-user-pools --max-results 1 --query 'UserPools[0].Id' --output text 2>/dev/null || echo "NOT_FOUND")
if [ "$POOL_ID" != "NOT_FOUND" ] && [ "$POOL_ID" != "None" ] && [ -n "$POOL_ID" ]; then
  echo "  Cognito Pool ID:     $POOL_ID"
else
  echo "  Cognito Pool ID:     (not available)"
fi

# LocalStack health endpoint
echo "  LocalStack Health:   ${AWS_ENDPOINT_URL}/_localstack/health"
echo "  LocalStack Region:   $AWS_REGION"

# CDK outputs file
if [ -f "$REPO_ROOT/cdk-outputs.json" ]; then
  echo ""
  echo "  📄 CDK outputs saved to: $REPO_ROOT/cdk-outputs.json"
  echo "     Use this file to discover resource IDs for other scripts"
fi

# =============================================================================
# Phase 7: Final Summary
# =============================================================================
# Prints completion status and next steps for the developer.
# The sibling scripts (seed-test-data.sh, run-migrations.sh) depend on
# resources created by this bootstrap script.
# =============================================================================

print_section "Bootstrap Complete"

if [ "$VERIFICATION_PASSED" = true ]; then
  echo ""
  echo "  🎉 LocalStack bootstrap complete! All resources verified."
else
  echo ""
  echo "  ⚠️  LocalStack bootstrap complete with warnings."
  echo "  Some resources may not have been created. Check the CDK output above."
fi

echo ""
echo "  Next steps:"
echo "    1. Run database migrations:  ./tools/scripts/run-migrations.sh"
echo "    2. Seed test data:           ./tools/scripts/seed-test-data.sh"
echo "    3. Start frontend dev:       cd apps/frontend && npm run dev"
echo ""
echo "  Environment:"
echo "    AWS_ENDPOINT_URL:  $AWS_ENDPOINT_URL"
echo "    AWS_REGION:        $AWS_REGION"
echo "    CDK Outputs:       $REPO_ROOT/cdk-outputs.json"
echo ""
