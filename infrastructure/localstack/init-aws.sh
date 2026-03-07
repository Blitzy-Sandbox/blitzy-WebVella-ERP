#!/bin/bash
# ==============================================================================
# LocalStack AWS Resource Provisioning Script
# WebVella ERP Microservices Architecture
# ==============================================================================
#
# Purpose:
#   Provisions all AWS resources (SNS topics, SQS queues, SNS→SQS subscriptions,
#   and S3 buckets) in LocalStack for the WebVella ERP microservices event-driven
#   architecture. This script is the single source of truth for the messaging
#   and storage infrastructure that replaces the monolith's in-process patterns.
#
# What this replaces:
#   - PostgreSQL LISTEN/NOTIFY pub/sub on channel 'ERP_NOTIFICATIONS_CHANNNEL'
#     (from WebVella.Erp/Notifications/NotificationContext.cs) → replaced by
#     SNS topics + SQS queues for asynchronous inter-service messaging
#   - 12 synchronous hook interfaces (WebVella.Erp/Hooks/IErp*Hook.cs):
#       IErpPreCreateRecordHook, IErpPostCreateRecordHook,
#       IErpPreUpdateRecordHook, IErpPostUpdateRecordHook,
#       IErpPreDeleteRecordHook, IErpPostDeleteRecordHook,
#       IErpPreSearchRecordHook, IErpPostSearchRecordHook,
#       IErpPreCreateManyToManyRelationHook, IErpPostCreateManyToManyRelationHook,
#       IErpPreDeleteManyToManyRelationHook, IErpPostDeleteManyToManyRelationHook
#     → replaced by domain event SNS topics with fan-out to per-service SQS queues
#   - DbFileRepository cloud storage backend (WebVella.Erp/Database/DbFileRepository.cs)
#     which used Storage.Net with cloud blob stores → replaced by S3 bucket
#
# Execution:
#   Automatically executed by LocalStack's init hook system when the container
#   reaches ready state. Mounted at:
#     /etc/localstack/init/ready.d/init-aws.sh
#   (see docker-compose.localstack.yml volume mount configuration)
#
# Idempotency:
#   All SNS/SQS create commands are idempotent in LocalStack — re-running this
#   script will not fail or create duplicate resources. S3 bucket creation uses
#   error suppression for the same bucket-already-exists case.
#
# Configuration:
#   AWS_ENDPOINT_URL  — LocalStack endpoint (default: http://localhost:4566)
#   AWS_DEFAULT_REGION — AWS region for all resources (default: us-east-1)
#   Both are injectable via environment variables per AAP Section 0.8.3.
#
# Per AAP Sections 0.4.1, 0.5.1, 0.7.4, and 0.8.3.
# ==============================================================================

set -euo pipefail

# ------------------------------------------------------------------------------
# Configuration
# ------------------------------------------------------------------------------
# The LocalStack endpoint and AWS region are injectable via environment variables
# to support switching between local development and production AWS endpoints
# (AAP Section 0.8.3 requirement). Defaults match localstack-config.yml settings.
LOCALSTACK_ENDPOINT="${AWS_ENDPOINT_URL:-http://localhost:4566}"
AWS_REGION="${AWS_DEFAULT_REGION:-us-east-1}"

# ------------------------------------------------------------------------------
# Logging Helper
# ------------------------------------------------------------------------------
log() {
    echo "[$(date '+%Y-%m-%d %H:%M:%S')] $*"
}

# ------------------------------------------------------------------------------
# Startup Banner
# ------------------------------------------------------------------------------
log "============================================================"
log "WebVella ERP — LocalStack Resource Provisioning"
log "============================================================"
log "Region:   ${AWS_REGION}"
log "Endpoint: ${LOCALSTACK_ENDPOINT}"
log "============================================================"

# ==============================================================================
# PHASE 1: Create SNS Topics (Domain Event Topics)
# ==============================================================================
# These 10 SNS topics replace the monolith's 12 synchronous hook interfaces
# (WebVella.Erp/Hooks/IErp*Hook.cs) and the PostgreSQL LISTEN/NOTIFY channel
# (ERP_NOTIFICATIONS_CHANNNEL) with asynchronous publish/subscribe messaging.
#
# Topic naming convention: erp-{domain}-{entity}-{action} or erp-{lifecycle}
# Each topic is published to by its owning service and consumed by subscriber
# services via SNS→SQS subscriptions defined in Phase 3.
# ==============================================================================

log ""
log "--- Phase 1: Creating SNS Topics (10 domain event topics) ---"

# Core record lifecycle events — published by Core service on every record CRUD.
# These replace the hook interfaces:
#   IErpPostCreateRecordHook.OnPostCreateRecord(string entityName, EntityRecord record)
#   IErpPostUpdateRecordHook.OnPostUpdateRecord(string entityName, EntityRecord record)
#   IErpPostDeleteRecordHook.OnPostDeleteRecord(string entityName, EntityRecord record)
RECORD_CREATED_ARN=$(awslocal sns create-topic \
    --name erp-record-created \
    --region "${AWS_REGION}" \
    --output text --query 'TopicArn')
log "  Created SNS topic: erp-record-created (ARN: ${RECORD_CREATED_ARN})"

RECORD_UPDATED_ARN=$(awslocal sns create-topic \
    --name erp-record-updated \
    --region "${AWS_REGION}" \
    --output text --query 'TopicArn')
log "  Created SNS topic: erp-record-updated (ARN: ${RECORD_UPDATED_ARN})"

RECORD_DELETED_ARN=$(awslocal sns create-topic \
    --name erp-record-deleted \
    --region "${AWS_REGION}" \
    --output text --query 'TopicArn')
log "  Created SNS topic: erp-record-deleted (ARN: ${RECORD_DELETED_ARN})"

# Entity lifecycle events — published by Core service when entity schemas change.
# These cover entity type provisioning and schema modifications managed by
# EntityManager.cs and exposed via gRPC for inter-service coordination.
ENTITY_CREATED_ARN=$(awslocal sns create-topic \
    --name erp-entity-created \
    --region "${AWS_REGION}" \
    --output text --query 'TopicArn')
log "  Created SNS topic: erp-entity-created (ARN: ${ENTITY_CREATED_ARN})"

ENTITY_UPDATED_ARN=$(awslocal sns create-topic \
    --name erp-entity-updated \
    --region "${AWS_REGION}" \
    --output text --query 'TopicArn')
log "  Created SNS topic: erp-entity-updated (ARN: ${ENTITY_UPDATED_ARN})"

# CRM-specific domain events — published by CRM service for cross-service
# entity relationships (AAP Section 0.7.1 Entity-to-Service Ownership Matrix).
# These replace the post-create/update hooks in WebVella.Erp.Plugins.Next/Hooks/Api/
# that previously handled account, contact, and case lifecycle events in-process.
CRM_ACCOUNT_UPDATED_ARN=$(awslocal sns create-topic \
    --name erp-crm-account-updated \
    --region "${AWS_REGION}" \
    --output text --query 'TopicArn')
log "  Created SNS topic: erp-crm-account-updated (ARN: ${CRM_ACCOUNT_UPDATED_ARN})"

CRM_CONTACT_UPDATED_ARN=$(awslocal sns create-topic \
    --name erp-crm-contact-updated \
    --region "${AWS_REGION}" \
    --output text --query 'TopicArn')
log "  Created SNS topic: erp-crm-contact-updated (ARN: ${CRM_CONTACT_UPDATED_ARN})"

CRM_CASE_UPDATED_ARN=$(awslocal sns create-topic \
    --name erp-crm-case-updated \
    --region "${AWS_REGION}" \
    --output text --query 'TopicArn')
log "  Created SNS topic: erp-crm-case-updated (ARN: ${CRM_CASE_UPDATED_ARN})"

# Project-specific domain event — published by Project service when tasks change.
# Enables cross-service notification to CRM (Case→Task reverse link) and
# Reporting (timelog/task aggregation) per AAP Section 0.7.1.
PROJECT_TASK_UPDATED_ARN=$(awslocal sns create-topic \
    --name erp-project-task-updated \
    --region "${AWS_REGION}" \
    --output text --query 'TopicArn')
log "  Created SNS topic: erp-project-task-updated (ARN: ${PROJECT_TASK_UPDATED_ARN})"

# Mail queue event — published by Mail service when an email is enqueued for
# sending. Replaces the MailPlugin's ProcessMailQueueJob self-triggering pattern
# and enables reporting/tracking of mail throughput.
MAIL_QUEUED_ARN=$(awslocal sns create-topic \
    --name erp-mail-queued \
    --region "${AWS_REGION}" \
    --output text --query 'TopicArn')
log "  Created SNS topic: erp-mail-queued (ARN: ${MAIL_QUEUED_ARN})"

SNS_TOPIC_COUNT=10
log "  Phase 1 complete: ${SNS_TOPIC_COUNT} SNS topics created"

# ==============================================================================
# PHASE 2: Create SQS Queues (Per-Service Event Queues)
# ==============================================================================
# Each microservice has a dedicated SQS queue for consuming domain events.
# Queue naming convention: erp-{service}-events
# Maps to the 6 microservices in the architecture (AAP Section 0.4.1):
#   Core, CRM, Project, Mail, Reporting, Admin
# ==============================================================================

log ""
log "--- Phase 2: Creating SQS Queues (6 per-service event queues) ---"

# Core service event queue — receives record lifecycle and entity lifecycle events
# for internal processing (cache invalidation, cross-entity coordination).
CORE_QUEUE_URL=$(awslocal sqs create-queue \
    --queue-name erp-core-events \
    --region "${AWS_REGION}" \
    --output text --query 'QueueUrl')
CORE_QUEUE_ARN=$(awslocal sqs get-queue-attributes \
    --queue-url "${CORE_QUEUE_URL}" \
    --attribute-names QueueArn \
    --region "${AWS_REGION}" \
    --output text --query 'Attributes.QueueArn')
log "  Created SQS queue: erp-core-events (ARN: ${CORE_QUEUE_ARN})"

# CRM service event queue — receives record events for CRM entity search index
# updates (per NextPlugin SearchService x_search field regeneration) and
# reverse notifications from Project service (Task→Case links).
CRM_QUEUE_URL=$(awslocal sqs create-queue \
    --queue-name erp-crm-events \
    --region "${AWS_REGION}" \
    --output text --query 'QueueUrl')
CRM_QUEUE_ARN=$(awslocal sqs get-queue-attributes \
    --queue-url "${CRM_QUEUE_URL}" \
    --attribute-names QueueArn \
    --region "${AWS_REGION}" \
    --output text --query 'Attributes.QueueArn')
log "  Created SQS queue: erp-crm-events (ARN: ${CRM_QUEUE_ARN})"

# Project service event queue — receives record events for task management,
# CRM cross-service events for Account→Project and Case→Task relations
# (AAP Section 0.7.1 cross-service relation resolution).
PROJECT_QUEUE_URL=$(awslocal sqs create-queue \
    --queue-name erp-project-events \
    --region "${AWS_REGION}" \
    --output text --query 'QueueUrl')
PROJECT_QUEUE_ARN=$(awslocal sqs get-queue-attributes \
    --queue-url "${PROJECT_QUEUE_URL}" \
    --attribute-names QueueArn \
    --region "${AWS_REGION}" \
    --output text --query 'Attributes.QueueArn')
log "  Created SQS queue: erp-project-events (ARN: ${PROJECT_QUEUE_ARN})"

# Mail service event queue — receives CRM contact events for Contact→Email
# cross-service relation and mail queue self-processing events.
MAIL_QUEUE_URL=$(awslocal sqs create-queue \
    --queue-name erp-mail-events \
    --region "${AWS_REGION}" \
    --output text --query 'QueueUrl')
MAIL_QUEUE_ARN=$(awslocal sqs get-queue-attributes \
    --queue-url "${MAIL_QUEUE_URL}" \
    --attribute-names QueueArn \
    --region "${AWS_REGION}" \
    --output text --query 'Attributes.QueueArn')
log "  Created SQS queue: erp-mail-events (ARN: ${MAIL_QUEUE_ARN})"

# Reporting service event queue — receives all domain events for CQRS-light
# read model projections and data aggregation (AAP Section 0.4.3 pattern).
REPORTING_QUEUE_URL=$(awslocal sqs create-queue \
    --queue-name erp-reporting-events \
    --region "${AWS_REGION}" \
    --output text --query 'QueueUrl')
REPORTING_QUEUE_ARN=$(awslocal sqs get-queue-attributes \
    --queue-url "${REPORTING_QUEUE_URL}" \
    --attribute-names QueueArn \
    --region "${AWS_REGION}" \
    --output text --query 'Attributes.QueueArn')
log "  Created SQS queue: erp-reporting-events (ARN: ${REPORTING_QUEUE_ARN})"

# Admin service event queue — receives entity lifecycle events for the Admin/SDK
# service to track entity schema changes, code generation triggers, and audit.
ADMIN_QUEUE_URL=$(awslocal sqs create-queue \
    --queue-name erp-admin-events \
    --region "${AWS_REGION}" \
    --output text --query 'QueueUrl')
ADMIN_QUEUE_ARN=$(awslocal sqs get-queue-attributes \
    --queue-url "${ADMIN_QUEUE_URL}" \
    --attribute-names QueueArn \
    --region "${AWS_REGION}" \
    --output text --query 'Attributes.QueueArn')
log "  Created SQS queue: erp-admin-events (ARN: ${ADMIN_QUEUE_ARN})"

SQS_QUEUE_COUNT=6
log "  Phase 2 complete: ${SQS_QUEUE_COUNT} SQS queues created"

# ==============================================================================
# PHASE 3: Create SNS → SQS Subscriptions
# ==============================================================================
# Wire SNS topics to SQS queues so each microservice receives the domain events
# it needs. The subscription matrix is based on the cross-service entity
# dependency analysis (AAP Section 0.7.1) and the hook-to-event mapping.
#
# Each subscription uses the 'sqs' protocol with the queue ARN as the
# notification endpoint. Event consumers must be idempotent per AAP 0.8.2.
#
# Total subscriptions: 26
# ==============================================================================

log ""
log "--- Phase 3: Creating SNS → SQS Subscriptions (26 event routes) ---"
SUBSCRIPTION_COUNT=0

# Helper function to create an SNS → SQS subscription with logging
subscribe() {
    local topic_arn="$1"
    local queue_arn="$2"
    local description="$3"
    awslocal sns subscribe \
        --topic-arn "${topic_arn}" \
        --protocol sqs \
        --notification-endpoint "${queue_arn}" \
        --region "${AWS_REGION}" \
        --output text --query 'SubscriptionArn' > /dev/null
    SUBSCRIPTION_COUNT=$((SUBSCRIPTION_COUNT + 1))
    log "  [${SUBSCRIPTION_COUNT}] ${description}"
}

# ---------------------------------------------------------------------------
# Core record events → multiple consumers
# ---------------------------------------------------------------------------
# erp-record-created fans out to Core, CRM, Project, and Reporting services.
# Core: internal cache invalidation and cross-entity coordination
# CRM: search index update (x_search field regeneration per SearchService)
# Project: task creation triggers from record events
# Reporting: aggregation and read model projection updates
log ""
log "  [Record Created Subscriptions]"
subscribe "${RECORD_CREATED_ARN}" "${CORE_QUEUE_ARN}" \
    "erp-record-created → erp-core-events (Core self-processing)"
subscribe "${RECORD_CREATED_ARN}" "${CRM_QUEUE_ARN}" \
    "erp-record-created → erp-crm-events (CRM search index update)"
subscribe "${RECORD_CREATED_ARN}" "${PROJECT_QUEUE_ARN}" \
    "erp-record-created → erp-project-events (Project task triggers)"
subscribe "${RECORD_CREATED_ARN}" "${REPORTING_QUEUE_ARN}" \
    "erp-record-created → erp-reporting-events (Reporting aggregation)"

# erp-record-updated fans out to Core, CRM, Project, and Reporting services.
log ""
log "  [Record Updated Subscriptions]"
subscribe "${RECORD_UPDATED_ARN}" "${CORE_QUEUE_ARN}" \
    "erp-record-updated → erp-core-events (Core self-processing)"
subscribe "${RECORD_UPDATED_ARN}" "${CRM_QUEUE_ARN}" \
    "erp-record-updated → erp-crm-events (CRM search index update)"
subscribe "${RECORD_UPDATED_ARN}" "${PROJECT_QUEUE_ARN}" \
    "erp-record-updated → erp-project-events (Project task triggers)"
subscribe "${RECORD_UPDATED_ARN}" "${REPORTING_QUEUE_ARN}" \
    "erp-record-updated → erp-reporting-events (Reporting aggregation)"

# erp-record-deleted fans out to Core, CRM, Project, and Reporting services.
log ""
log "  [Record Deleted Subscriptions]"
subscribe "${RECORD_DELETED_ARN}" "${CORE_QUEUE_ARN}" \
    "erp-record-deleted → erp-core-events (Core self-processing)"
subscribe "${RECORD_DELETED_ARN}" "${CRM_QUEUE_ARN}" \
    "erp-record-deleted → erp-crm-events (CRM search index update)"
subscribe "${RECORD_DELETED_ARN}" "${PROJECT_QUEUE_ARN}" \
    "erp-record-deleted → erp-project-events (Project task triggers)"
subscribe "${RECORD_DELETED_ARN}" "${REPORTING_QUEUE_ARN}" \
    "erp-record-deleted → erp-reporting-events (Reporting aggregation)"

# ---------------------------------------------------------------------------
# Entity lifecycle events → Core and Admin consumers
# ---------------------------------------------------------------------------
# Entity creation and update events notify Core (metadata cache invalidation)
# and Admin/SDK (entity designer, code generation triggers).
log ""
log "  [Entity Lifecycle Subscriptions]"
subscribe "${ENTITY_CREATED_ARN}" "${CORE_QUEUE_ARN}" \
    "erp-entity-created → erp-core-events (Core metadata cache)"
subscribe "${ENTITY_CREATED_ARN}" "${ADMIN_QUEUE_ARN}" \
    "erp-entity-created → erp-admin-events (Admin schema tracking)"
subscribe "${ENTITY_UPDATED_ARN}" "${CORE_QUEUE_ARN}" \
    "erp-entity-updated → erp-core-events (Core metadata cache)"
subscribe "${ENTITY_UPDATED_ARN}" "${ADMIN_QUEUE_ARN}" \
    "erp-entity-updated → erp-admin-events (Admin schema tracking)"

# ---------------------------------------------------------------------------
# CRM-specific events → cross-service consumers
# ---------------------------------------------------------------------------
# Account updates notify Project (Account→Project relation, AAP 0.7.1) and
# Reporting (aggregation). Contact updates notify Mail (Contact→Email relation)
# and Reporting. Case updates notify Project (Case→Task relation) and Reporting.
log ""
log "  [CRM Domain Event Subscriptions]"
subscribe "${CRM_ACCOUNT_UPDATED_ARN}" "${PROJECT_QUEUE_ARN}" \
    "erp-crm-account-updated → erp-project-events (Account→Project relation)"
subscribe "${CRM_ACCOUNT_UPDATED_ARN}" "${REPORTING_QUEUE_ARN}" \
    "erp-crm-account-updated → erp-reporting-events (Reporting aggregation)"
subscribe "${CRM_CONTACT_UPDATED_ARN}" "${MAIL_QUEUE_ARN}" \
    "erp-crm-contact-updated → erp-mail-events (Contact→Email relation)"
subscribe "${CRM_CONTACT_UPDATED_ARN}" "${REPORTING_QUEUE_ARN}" \
    "erp-crm-contact-updated → erp-reporting-events (Reporting aggregation)"
subscribe "${CRM_CASE_UPDATED_ARN}" "${PROJECT_QUEUE_ARN}" \
    "erp-crm-case-updated → erp-project-events (Case→Task relation)"
subscribe "${CRM_CASE_UPDATED_ARN}" "${REPORTING_QUEUE_ARN}" \
    "erp-crm-case-updated → erp-reporting-events (Reporting aggregation)"

# ---------------------------------------------------------------------------
# Project-specific events → CRM and Reporting consumers
# ---------------------------------------------------------------------------
# Task updates notify CRM (Task→Case reverse notification) and Reporting
# (task/timelog aggregation for dashboards and reports).
log ""
log "  [Project Domain Event Subscriptions]"
subscribe "${PROJECT_TASK_UPDATED_ARN}" "${CRM_QUEUE_ARN}" \
    "erp-project-task-updated → erp-crm-events (Task→Case reverse notification)"
subscribe "${PROJECT_TASK_UPDATED_ARN}" "${REPORTING_QUEUE_ARN}" \
    "erp-project-task-updated → erp-reporting-events (Reporting aggregation)"

# ---------------------------------------------------------------------------
# Mail events → Mail and Reporting consumers
# ---------------------------------------------------------------------------
# Mail queue events notify Mail service (self-processing for queue dispatch)
# and Reporting (mail throughput tracking).
log ""
log "  [Mail Domain Event Subscriptions]"
subscribe "${MAIL_QUEUED_ARN}" "${MAIL_QUEUE_ARN}" \
    "erp-mail-queued → erp-mail-events (Mail queue self-processing)"
subscribe "${MAIL_QUEUED_ARN}" "${REPORTING_QUEUE_ARN}" \
    "erp-mail-queued → erp-reporting-events (Reporting tracking)"

log "  Phase 3 complete: ${SUBSCRIPTION_COUNT} SNS→SQS subscriptions created"

# ==============================================================================
# PHASE 4: Create S3 Buckets (File Storage)
# ==============================================================================
# S3 buckets replace the monolith's DbFileRepository cloud storage backend
# (WebVella.Erp/Database/DbFileRepository.cs) which used Storage.Net with
# cloud blob stores. The monolith organized files using '/' folder separators
# (FOLDER_SEPARATOR = "/") and had a TMP_FOLDER_NAME = "tmp" for temporary files.
#
# A single 'erp-files' bucket serves as the shared file storage backend for
# all services, matching the docker-compose.localstack.yml configuration.
# ==============================================================================

log ""
log "--- Phase 4: Creating S3 Buckets (1 file storage bucket) ---"

awslocal s3 mb "s3://erp-files" \
    --region "${AWS_REGION}" 2>/dev/null || true
log "  Created S3 bucket: erp-files"

S3_BUCKET_COUNT=1
log "  Phase 4 complete: ${S3_BUCKET_COUNT} S3 bucket created"

# ==============================================================================
# PHASE 5: Completion Summary
# ==============================================================================

log ""
log "============================================================"
log "WebVella ERP — LocalStack Provisioning Summary"
log "============================================================"
log "  SNS Topics:          ${SNS_TOPIC_COUNT}"
log "  SQS Queues:          ${SQS_QUEUE_COUNT}"
log "  SNS→SQS Subs:        ${SUBSCRIPTION_COUNT}"
log "  S3 Buckets:          ${S3_BUCKET_COUNT}"
log "  Total Resources:     $((SNS_TOPIC_COUNT + SQS_QUEUE_COUNT + SUBSCRIPTION_COUNT + S3_BUCKET_COUNT))"
log "============================================================"
log ""
log "Resource Details:"
log "  SNS Topics: erp-record-created, erp-record-updated, erp-record-deleted,"
log "              erp-entity-created, erp-entity-updated,"
log "              erp-crm-account-updated, erp-crm-contact-updated, erp-crm-case-updated,"
log "              erp-project-task-updated, erp-mail-queued"
log "  SQS Queues: erp-core-events, erp-crm-events, erp-project-events,"
log "              erp-mail-events, erp-reporting-events, erp-admin-events"
log "  S3 Buckets: erp-files"
log ""
log "LocalStack initialization complete."
log "============================================================"

exit 0
