using System.Text.Json.Serialization;

namespace WebVellaErp.Workflow.Models
{
    /// <summary>
    /// Configuration settings for the Workflow Engine microservice.
    /// Replaces the monolith's <c>JobManagerSettings</c> class from
    /// <c>WebVella.Erp/Jobs/Models/JobSettings.cs</c>.
    ///
    /// Key changes from source:
    /// - Removed <c>DbConnectionString</c> (DynamoDB does not use connection strings).
    /// - Added DynamoDB table name, Step Functions state machine ARN, SNS topic ARN,
    ///   SQS queue URL, AWS region, and AWS endpoint URL for LocalStack dual-target support.
    ///
    /// Sensitive secrets (e.g., DB_CONNECTION_STRING, COGNITO_CLIENT_SECRET) are NOT stored
    /// in this settings class. Per AAP Section 0.8.6, all secrets are resolved at runtime
    /// from AWS Systems Manager (SSM) Parameter Store as SecureString values and are never
    /// placed in environment variables or configuration DTOs.
    /// </summary>
    public class WorkflowSettings
    {
        /// <summary>
        /// Controls whether workflow processing is active.
        /// Preserved from the monolith's <c>JobManagerSettings.Enabled</c> property.
        /// When <c>false</c>, the service will not process new workflows or schedule triggers.
        /// </summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        /// <summary>
        /// The DynamoDB table name used for workflow state and metadata persistence.
        /// Replaces the monolith's PostgreSQL <c>jobs</c> and <c>schedule_plans</c> tables
        /// with a single-table design using PK/SK patterns.
        /// </summary>
        [JsonPropertyName("dynamodb_table_name")]
        public string? DynamoDbTableName { get; set; }

        /// <summary>
        /// The ARN of the AWS Step Functions state machine used for workflow orchestration.
        /// Replaces the monolith's in-process <c>JobPool</c> bounded 20-thread executor
        /// with serverless state machine orchestration via <c>StartExecution</c>,
        /// <c>StopExecution</c>, <c>SendTaskSuccess</c>, and <c>SendTaskFailure</c>.
        /// </summary>
        [JsonPropertyName("step_functions_state_machine_arn")]
        public string? StepFunctionsStateMachineArn { get; set; }

        /// <summary>
        /// The ARN of the SNS topic for publishing workflow domain events.
        /// Events follow the naming convention <c>{domain}.{entity}.{action}</c>
        /// (e.g., <c>workflow.workflow.started</c>, <c>workflow.workflow.completed</c>,
        /// <c>workflow.workflow.failed</c>) per AAP Section 0.8.5.
        /// Replaces the monolith's synchronous in-process <c>HookManager</c> post-CRUD hooks.
        /// </summary>
        [JsonPropertyName("sns_topic_arn")]
        public string? SnsTopicArn { get; set; }

        /// <summary>
        /// The URL of the SQS queue used for queue-triggered step execution.
        /// Replaces the monolith's <c>JobPool Task.Run(() => Process(context))</c> dispatch
        /// pattern with SQS-triggered Lambda invocations. Dead-letter queues follow the
        /// naming convention <c>workflow-{queue}-dlq</c> per AAP Section 0.8.5.
        /// </summary>
        [JsonPropertyName("sqs_queue_url")]
        public string? SqsQueueUrl { get; set; }

        /// <summary>
        /// The AWS region for service operations.
        /// Defaults to <c>us-east-1</c> per AAP Section 0.8.6.
        /// </summary>
        [JsonPropertyName("aws_region")]
        public string? AwsRegion { get; set; }

        /// <summary>
        /// The AWS endpoint URL override for LocalStack compatibility.
        /// Set to <c>http://localhost:4566</c> when <c>IS_LOCAL=true</c> per AAP Section 0.8.6.
        /// When <c>null</c> or empty, the default AWS SDK endpoint resolution is used
        /// (production mode).
        /// </summary>
        [JsonPropertyName("aws_endpoint_url")]
        public string? AwsEndpointUrl { get; set; }
    }
}
