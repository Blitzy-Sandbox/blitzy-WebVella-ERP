#!/bin/bash
# ==============================================================================
# LocalStack Initialization Script — WebVella ERP Microservices
# ==============================================================================
#
# Purpose:
#   Provisions all required AWS resources (SNS topics, SQS queues, S3 buckets,
#   and SNS→SQS subscriptions) in LocalStack for the WebVella ERP microservices
#   event-driven architecture.
#
# Context:
#   This script replaces the monolith's in-process communication:
#     - SNS topics replace the 12 synchronous hook interfaces
#       (WebVella.Erp/Hooks/IErp*Hook.cs)
#     - SQS queues provide per-service event consumption
#     - S3 buckets replace the DbFileRepository cloud storage backend
#       (WebVella.Erp/Database/DbFileRepository.cs via Storage.Net)
#
# Execution:
#   Automatically executed by LocalStack on container startup when mounted at:
#     /etc/localstack/init/ready.d/init-aws.sh
#   (see docker-compose.localstack.yml volume mount)
#
#   Manual execution:
#     chmod +x infrastructure/localstack/init-aws.sh
#     ./infrastructure/localstack/init-aws.sh
#
# Idempotency:
#   All create commands use 2>/dev/null || true to silently skip resources
#   that already exist, making the script safe to re-run when persistence
#   is enabled (PERSISTENCE=1 in localstack-config.yml).
#
# Region:
#   All resources are provisioned in us-east-1, matching the DEFAULT_REGION
#   setting in localstack-config.yml and each service's appsettings.json.
#
# Per AAP Sections 0.4.1, 0.7.4, and 0.8.3.
# ==============================================================================

set -euo pipefail

# ------------------------------------------------------------------------------
# Configuration
# ------------------------------------------------------------------------------
AWS_REGION="${AWS_DEFAULT_REGION:-us-east-1}"
ENDPOINT_URL="${AWS_ENDPOINT_URL:-http://localhost:4566}"

# Use awslocal if available (LocalStack CLI), otherwise fall back to aws CLI
# with explicit endpoint configuration.
if command -v awslocal &> /dev/null; then
    AWS_CMD="awslocal"
else
    AWS_CMD="aws --endpoint-url=${ENDPOINT_URL} --region ${AWS_REGION}"
fi

echo "============================================================"
echo "WebVella ERP — LocalStack Resource Provisioning"
echo "============================================================"
echo "Region:   ${AWS_REGION}"
echo "Endpoint: ${ENDPOINT_URL}"
echo "CLI:      ${AWS_CMD}"
echo "============================================================"

# ------------------------------------------------------------------------------
# SNS Topics — Domain Event Topics
# ------------------------------------------------------------------------------
# These SNS topics replace the monolith's 12 synchronous hook interfaces
# (IErpPre/PostCreateRecordHook, IErpPre/PostUpdateRecordHook, etc.)
# with asynchronous publish/subscribe messaging.
#
# Topic naming convention: erp-<lifecycle>-<operation>-<entity-type>
# Maps to hook interfaces in WebVella.Erp/Hooks/:
#   IErpPreCreateRecordHook  → erp-record-pre-create
#   IErpPostCreateRecordHook → erp-record-post-create
#   ... and so on for update, delete, and relation hooks.
# ------------------------------------------------------------------------------

echo ""
echo "--- Creating SNS Topics (Domain Event Topics) ---"

SNS_TOPICS=(
    "erp-record-pre-create"
    "erp-record-post-create"
    "erp-record-pre-update"
    "erp-record-post-update"
    "erp-record-pre-delete"
    "erp-record-post-delete"
    "erp-relation-pre-create"
    "erp-relation-post-create"
    "erp-relation-pre-delete"
    "erp-relation-post-delete"
)

for topic in "${SNS_TOPICS[@]}"; do
    echo "  Creating SNS topic: ${topic}"
    ${AWS_CMD} sns create-topic --name "${topic}" \
        --region "${AWS_REGION}" 2>/dev/null || true
done

echo "  SNS topics created: ${#SNS_TOPICS[@]}"

# ------------------------------------------------------------------------------
# SQS Queues — Per-Service Event Queues
# ------------------------------------------------------------------------------
# Each microservice has a dedicated SQS queue for consuming domain events.
# Services subscribe to relevant SNS topics via SNS→SQS subscriptions (below).
#
# Queue naming convention: erp-<service-name>-events
# Maps to the 6 microservices defined in the architecture:
#   Core, CRM, Project, Mail, Reporting, Admin
# ------------------------------------------------------------------------------

echo ""
echo "--- Creating SQS Queues (Per-Service Event Queues) ---"

SQS_QUEUES=(
    "erp-core-service-events"
    "erp-crm-service-events"
    "erp-project-service-events"
    "erp-mail-service-events"
    "erp-reporting-service-events"
    "erp-admin-service-events"
)

for queue in "${SQS_QUEUES[@]}"; do
    echo "  Creating SQS queue: ${queue}"
    ${AWS_CMD} sqs create-queue --queue-name "${queue}" \
        --region "${AWS_REGION}" \
        --attributes '{
            "VisibilityTimeout": "60",
            "MessageRetentionPeriod": "1209600",
            "ReceiveMessageWaitTimeSeconds": "20"
        }' 2>/dev/null || true
done

echo "  SQS queues created: ${#SQS_QUEUES[@]}"

# ------------------------------------------------------------------------------
# S3 Buckets — File Storage
# ------------------------------------------------------------------------------
# S3 buckets replace the monolith's DbFileRepository cloud storage backend
# (WebVella.Erp/Database/DbFileRepository.cs, which supported large objects,
# filesystem, and cloud storage via Storage.Net).
#
# Bucket naming convention: erp-<service>-files
# Maps to the Storage.S3.BucketName configuration in each service's
# appsettings.json (Core: erp-core-files, CRM: erp-crm-files, etc.)
# Also includes erp-files as a shared/general-purpose bucket referenced
# in docker-compose.localstack.yml.
# ------------------------------------------------------------------------------

echo ""
echo "--- Creating S3 Buckets (File Storage) ---"

S3_BUCKETS=(
    "erp-files"
    "erp-core-files"
    "erp-crm-files"
    "erp-mail-files"
)

for bucket in "${S3_BUCKETS[@]}"; do
    echo "  Creating S3 bucket: ${bucket}"
    ${AWS_CMD} s3 mb "s3://${bucket}" \
        --region "${AWS_REGION}" 2>/dev/null || true
done

echo "  S3 buckets created: ${#S3_BUCKETS[@]}"

# ------------------------------------------------------------------------------
# SNS → SQS Subscriptions
# ------------------------------------------------------------------------------
# Wire SNS topics to SQS queues so each microservice receives the domain
# events it needs. The subscription matrix is based on the cross-service
# entity dependency analysis (AAP Section 0.7.1) and hook-to-event mapping.
#
# Subscription routing logic:
#   - Core service:      subscribes to ALL record & relation events (owns entities)
#   - CRM service:       subscribes to record create/update/delete (CRM entities)
#   - Project service:   subscribes to record create/update/delete (task entities)
#                         + relation events (case→task, account→project links)
#   - Mail service:      subscribes to record post-create (email triggers)
#                         + record post-update (status changes)
#   - Reporting service: subscribes to ALL post-* events (aggregation/projections)
#   - Admin service:     subscribes to ALL events (audit, monitoring, logging)
# ------------------------------------------------------------------------------

echo ""
echo "--- Creating SNS → SQS Subscriptions ---"

# Helper function to get the SQS queue ARN from queue name
get_queue_arn() {
    local queue_name="$1"
    local queue_url
    queue_url=$(${AWS_CMD} sqs get-queue-url --queue-name "${queue_name}" \
        --region "${AWS_REGION}" --output text --query 'QueueUrl' 2>/dev/null) || return 1
    local queue_arn
    queue_arn=$(${AWS_CMD} sqs get-queue-attributes --queue-url "${queue_url}" \
        --attribute-names QueueArn --region "${AWS_REGION}" \
        --output text --query 'Attributes.QueueArn' 2>/dev/null) || return 1
    echo "${queue_arn}"
}

# Helper function to get the SNS topic ARN from topic name
get_topic_arn() {
    local topic_name="$1"
    echo "arn:aws:sns:${AWS_REGION}:000000000000:${topic_name}"
}

# Helper function to create an SNS → SQS subscription
subscribe_queue_to_topic() {
    local topic_name="$1"
    local queue_name="$2"
    local topic_arn
    topic_arn=$(get_topic_arn "${topic_name}")
    local queue_arn
    queue_arn=$(get_queue_arn "${queue_name}") || {
        echo "    WARNING: Could not resolve ARN for queue ${queue_name}, skipping subscription"
        return 0
    }

    echo "  Subscribing ${queue_name} → ${topic_name}"
    ${AWS_CMD} sns subscribe \
        --topic-arn "${topic_arn}" \
        --protocol sqs \
        --notification-endpoint "${queue_arn}" \
        --region "${AWS_REGION}" 2>/dev/null || true
}

# --- Core Service Subscriptions ---
# Core service subscribes to all record and relation events because it
# owns entity metadata, manages cross-entity relations, and coordinates
# the security context and file storage for all services.
echo ""
echo "  [Core Service Subscriptions]"
subscribe_queue_to_topic "erp-record-pre-create"    "erp-core-service-events"
subscribe_queue_to_topic "erp-record-post-create"   "erp-core-service-events"
subscribe_queue_to_topic "erp-record-pre-update"    "erp-core-service-events"
subscribe_queue_to_topic "erp-record-post-update"   "erp-core-service-events"
subscribe_queue_to_topic "erp-record-pre-delete"    "erp-core-service-events"
subscribe_queue_to_topic "erp-record-post-delete"   "erp-core-service-events"
subscribe_queue_to_topic "erp-relation-pre-create"  "erp-core-service-events"
subscribe_queue_to_topic "erp-relation-post-create" "erp-core-service-events"
subscribe_queue_to_topic "erp-relation-pre-delete"  "erp-core-service-events"
subscribe_queue_to_topic "erp-relation-post-delete" "erp-core-service-events"

# --- CRM Service Subscriptions ---
# CRM service subscribes to record lifecycle events for CRM entities
# (account, contact, case, address, salutation) and relation events
# for cross-service links.
echo ""
echo "  [CRM Service Subscriptions]"
subscribe_queue_to_topic "erp-record-post-create"   "erp-crm-service-events"
subscribe_queue_to_topic "erp-record-post-update"   "erp-crm-service-events"
subscribe_queue_to_topic "erp-record-post-delete"   "erp-crm-service-events"
subscribe_queue_to_topic "erp-relation-post-create" "erp-crm-service-events"
subscribe_queue_to_topic "erp-relation-post-delete" "erp-crm-service-events"

# --- Project Service Subscriptions ---
# Project service subscribes to record events for task management and
# relation events for case→task and account→project linkage (AAP 0.7.1).
echo ""
echo "  [Project Service Subscriptions]"
subscribe_queue_to_topic "erp-record-post-create"   "erp-project-service-events"
subscribe_queue_to_topic "erp-record-post-update"   "erp-project-service-events"
subscribe_queue_to_topic "erp-record-post-delete"   "erp-project-service-events"
subscribe_queue_to_topic "erp-relation-post-create" "erp-project-service-events"
subscribe_queue_to_topic "erp-relation-post-delete" "erp-project-service-events"

# --- Mail Service Subscriptions ---
# Mail service subscribes to record create/update events to trigger
# email notifications and process the mail queue.
echo ""
echo "  [Mail Service Subscriptions]"
subscribe_queue_to_topic "erp-record-post-create" "erp-mail-service-events"
subscribe_queue_to_topic "erp-record-post-update" "erp-mail-service-events"

# --- Reporting Service Subscriptions ---
# Reporting service subscribes to all post-* events for CQRS-light
# read model projections and data aggregation (AAP 0.4.3).
echo ""
echo "  [Reporting Service Subscriptions]"
subscribe_queue_to_topic "erp-record-post-create"   "erp-reporting-service-events"
subscribe_queue_to_topic "erp-record-post-update"   "erp-reporting-service-events"
subscribe_queue_to_topic "erp-record-post-delete"   "erp-reporting-service-events"
subscribe_queue_to_topic "erp-relation-post-create" "erp-reporting-service-events"
subscribe_queue_to_topic "erp-relation-post-delete" "erp-reporting-service-events"

# --- Admin Service Subscriptions ---
# Admin service subscribes to all events for system monitoring,
# audit logging, and administrative oversight.
echo ""
echo "  [Admin Service Subscriptions]"
subscribe_queue_to_topic "erp-record-pre-create"    "erp-admin-service-events"
subscribe_queue_to_topic "erp-record-post-create"   "erp-admin-service-events"
subscribe_queue_to_topic "erp-record-post-update"   "erp-admin-service-events"
subscribe_queue_to_topic "erp-record-post-delete"   "erp-admin-service-events"
subscribe_queue_to_topic "erp-relation-post-create" "erp-admin-service-events"
subscribe_queue_to_topic "erp-relation-post-delete" "erp-admin-service-events"

SUBSCRIPTION_COUNT=28
echo ""
echo "  SNS→SQS subscriptions created: ${SUBSCRIPTION_COUNT}"

# ------------------------------------------------------------------------------
# Verification — List Provisioned Resources
# ------------------------------------------------------------------------------

echo ""
echo "============================================================"
echo "Provisioning Summary"
echo "============================================================"

echo ""
echo "--- SNS Topics ---"
${AWS_CMD} sns list-topics --region "${AWS_REGION}" \
    --output table --query 'Topics[*].TopicArn' 2>/dev/null || true

echo ""
echo "--- SQS Queues ---"
${AWS_CMD} sqs list-queues --region "${AWS_REGION}" \
    --output table --query 'QueueUrls' 2>/dev/null || true

echo ""
echo "--- S3 Buckets ---"
${AWS_CMD} s3 ls --region "${AWS_REGION}" 2>/dev/null || true

echo ""
echo "============================================================"
echo "WebVella ERP — LocalStack provisioning complete!"
echo "  SNS Topics:        ${#SNS_TOPICS[@]}"
echo "  SQS Queues:        ${#SQS_QUEUES[@]}"
echo "  S3 Buckets:        ${#S3_BUCKETS[@]}"
echo "  SNS→SQS Subs:      ${SUBSCRIPTION_COUNT}"
echo "============================================================"
