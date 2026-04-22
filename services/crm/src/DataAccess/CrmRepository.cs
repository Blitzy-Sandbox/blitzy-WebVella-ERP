using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WebVellaErp.Crm.Models;

namespace WebVellaErp.Crm.DataAccess
{
    // =========================================================================
    // Enums — Filter, Sort, and Logic types
    // =========================================================================

    /// <summary>
    /// Defines comparison operators for DynamoDB filter expressions.
    /// Maps to the monolith's QueryType enum values from GenerateWhereClause
    /// (EQ/NOT/LT/LTE/GT/GTE/CONTAINS/STARTSWITH/REGEX/FTS).
    /// </summary>
    public enum FilterOperator
    {
        /// <summary>Equality comparison (=). Replaces QueryType.EQ → SQL "= @param".</summary>
        Equal,
        /// <summary>Inequality comparison (<>). Replaces QueryType.NOT.</summary>
        NotEqual,
        /// <summary>Less-than comparison (<). Replaces QueryType.LT.</summary>
        LessThan,
        /// <summary>Less-than-or-equal comparison (<=). Replaces QueryType.LTE.</summary>
        LessThanOrEqual,
        /// <summary>Greater-than comparison (>). Replaces QueryType.GT.</summary>
        GreaterThan,
        /// <summary>Greater-than-or-equal comparison (>=). Replaces QueryType.GTE.</summary>
        GreaterThanOrEqual,
        /// <summary>Substring containment. Replaces QueryType.CONTAINS → SQL LIKE '%val%'.</summary>
        Contains,
        /// <summary>Prefix match. Replaces QueryType.STARTSWITH → SQL LIKE 'val%'.</summary>
        StartsWith,
        /// <summary>Regex pattern match. Not natively supported by DynamoDB — falls back to client-side filtering.</summary>
        Regex,
        /// <summary>Full-text search approximation. Uses DynamoDB contains() on x_search field as fallback for PostgreSQL to_tsvector/to_tsquery.</summary>
        Fts
    }

    /// <summary>
    /// Logical connective for combining sub-filter expressions.
    /// Maps to the monolith's QueryObjectSubFilterLogic enum.
    /// </summary>
    public enum FilterLogic
    {
        /// <summary>All sub-filters must be true (AND connective).</summary>
        And,
        /// <summary>At least one sub-filter must be true (OR connective).</summary>
        Or
    }

    /// <summary>
    /// Sort direction for query result ordering.
    /// Maps to the monolith's QuerySortType enum.
    /// </summary>
    public enum SortDirection
    {
        /// <summary>Ascending order (A → Z, 0 → 9, earliest → latest).</summary>
        Ascending,
        /// <summary>Descending order (Z → A, 9 → 0, latest → earliest).</summary>
        Descending
    }

    // =========================================================================
    // Supporting Types — Query, Sort, Pagination, and Result models
    // =========================================================================

    /// <summary>
    /// Represents a composable filter tree for DynamoDB queries.
    /// Mirrors the monolith's QueryObject structure from WebVella.Erp/Api/Models/QueryObject.cs.
    /// Supports both leaf filters (FieldName + Operator + Value) and nested groups (SubFilters + Logic).
    /// </summary>
    public class QueryFilter
    {
        /// <summary>
        /// The field name to filter on. Must match the DynamoDB attribute name
        /// (snake_case, matching [JsonPropertyName] on model properties).
        /// Null when this filter is a group node with SubFilters.
        /// </summary>
        public string? FieldName { get; set; }

        /// <summary>
        /// The comparison operator to apply. Used for leaf filter nodes.
        /// </summary>
        public FilterOperator Operator { get; set; }

        /// <summary>
        /// The value to compare the field against. Type depends on the field type:
        /// string, Guid (as string), DateTime (as ISO 8601 string), decimal, bool.
        /// Null is valid for existence checks.
        /// </summary>
        public object? Value { get; set; }

        /// <summary>
        /// Nested sub-filters combined using the <see cref="Logic"/> connective.
        /// When non-null, this filter acts as a group node (ignoring FieldName/Operator/Value).
        /// Mirrors the recursive GenerateWhereClause pattern from DbRecordRepository (lines 1521-1560).
        /// </summary>
        public List<QueryFilter>? SubFilters { get; set; }

        /// <summary>
        /// Logical connective for combining sub-filters (AND or OR).
        /// Default is AND, matching the monolith's default behavior.
        /// </summary>
        public FilterLogic Logic { get; set; } = FilterLogic.And;
    }

    /// <summary>
    /// Specifies sorting options for query results.
    /// Maps to the monolith's QuerySort/QuerySortType from DbRecordRepository (lines 683-733).
    /// </summary>
    public class SortOptions
    {
        /// <summary>
        /// The field name to sort by. Must match the DynamoDB attribute name.
        /// For "created_on", GSI1 sort key is used; all other fields use in-memory LINQ sort.
        /// </summary>
        public string FieldName { get; set; } = string.Empty;

        /// <summary>
        /// The direction to sort results. Default is Ascending.
        /// </summary>
        public SortDirection Direction { get; set; } = SortDirection.Ascending;
    }

    /// <summary>
    /// Specifies pagination options for DynamoDB queries.
    /// Replaces SQL LIMIT/OFFSET from the monolith's DbRecordRepository (lines 735-748)
    /// with DynamoDB-native cursor-based pagination.
    /// </summary>
    public class PaginationOptions
    {
        /// <summary>
        /// Maximum number of records to return. Maps to DynamoDB Query Limit parameter.
        /// Replaces SQL LIMIT from source.
        /// </summary>
        public int? Limit { get; set; }

        /// <summary>
        /// Number of records to skip from the beginning. DynamoDB does not support OFFSET natively;
        /// this is implemented by fetching and discarding records.
        /// Replaces SQL OFFSET from source.
        /// </summary>
        public int? Skip { get; set; }

        /// <summary>
        /// DynamoDB-native cursor-based pagination token. Encoded as a Base64 JSON string
        /// containing the LastEvaluatedKey from a previous query response.
        /// This is the preferred pagination approach for DynamoDB (replaces OFFSET).
        /// </summary>
        public string? ExclusiveStartKey { get; set; }
    }

    /// <summary>
    /// Encapsulates a page of query results with DynamoDB pagination metadata.
    /// </summary>
    /// <typeparam name="T">The entity type for the result items.</typeparam>
    public class PagedResult<T>
    {
        /// <summary>
        /// The list of deserialized entity records for this page.
        /// </summary>
        public List<T> Items { get; set; } = new();

        /// <summary>
        /// The DynamoDB pagination continuation token. Null when no more pages remain.
        /// Pass this value as PaginationOptions.ExclusiveStartKey for the next page.
        /// </summary>
        public string? LastEvaluatedKey { get; set; }

        /// <summary>
        /// Total count of records matching the query across all pages.
        /// </summary>
        public long TotalCount { get; set; }
    }

    // =========================================================================
    // Interface — ICrmRepository
    // =========================================================================

    /// <summary>
    /// Defines the DynamoDB data access contract for all CRM bounded-context entities.
    /// Designed for constructor injection via ASP.NET Core DI (NOT the monolith's
    /// ambient DbContext.Current singleton pattern).
    /// </summary>
    public interface ICrmRepository
    {
        /// <summary>
        /// Retrieves a single record by its unique identifier.
        /// Replaces DbRecordRepository.Find(string entityName, Guid id).
        /// </summary>
        Task<T?> GetByIdAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string entityName, Guid recordId, CancellationToken ct = default) where T : class, new();

        /// <summary>
        /// Queries records with optional filtering, sorting, and pagination.
        /// Replaces DbRecordRepository.Find(EntityQuery query) and GenerateWhereClause.
        /// </summary>
        Task<List<T>> QueryAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string entityName, QueryFilter? filter = null, SortOptions? sort = null, PaginationOptions? pagination = null, CancellationToken ct = default) where T : class, new();

        /// <summary>
        /// Returns the total count of records matching optional filter criteria.
        /// Replaces DbRecordRepository.Count() with SELECT COUNT(*).
        /// </summary>
        Task<long> CountAsync(string entityName, QueryFilter? filter = null, CancellationToken ct = default);

        /// <summary>
        /// Creates a new record with idempotent conditional write (attribute_not_exists).
        /// Replaces DbRecordRepository.Create().
        /// </summary>
        Task CreateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string entityName, T record, CancellationToken ct = default) where T : class;

        /// <summary>
        /// Updates an existing record by ID with conditional expression for existence validation.
        /// Replaces DbRecordRepository.Update().
        /// </summary>
        Task UpdateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string entityName, Guid recordId, T record, CancellationToken ct = default) where T : class;

        /// <summary>
        /// Deletes a record by ID after verifying existence (matching source pattern).
        /// Replaces DbRecordRepository.Delete().
        /// </summary>
        Task DeleteAsync(string entityName, Guid recordId, CancellationToken ct = default);

        /// <summary>
        /// Creates multiple records in batch with DynamoDB BatchWriteItem (max 25 per batch).
        /// </summary>
        Task BatchCreateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string entityName, IEnumerable<T> records, CancellationToken ct = default) where T : class;

        /// <summary>
        /// Retrieves multiple records by their IDs with DynamoDB BatchGetItem (max 100 per batch).
        /// </summary>
        Task<List<T>> BatchGetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string entityName, IEnumerable<Guid> recordIds, CancellationToken ct = default) where T : class, new();

        /// <summary>
        /// Searches records using the x_search composite field via GSI2 prefix-based lookup.
        /// Replaces the PostgreSQL x_search LIKE query pattern from SearchService.cs.
        /// </summary>
        Task<List<T>> SearchAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string entityName, string searchText, PaginationOptions? pagination = null, CancellationToken ct = default) where T : class, new();

        /// <summary>
        /// Updates the x_search composite search index field for a specific record.
        /// Used by the SearchService to regenerate searchable text after record mutations.
        /// </summary>
        Task UpdateSearchFieldAsync(string entityName, Guid recordId, string searchFieldValue, CancellationToken ct = default);
    }

    // =========================================================================
    // Implementation — CrmRepository
    // =========================================================================

    /// <summary>
    /// DynamoDB single-table design implementation for all CRM bounded-context data access.
    /// Replaces the monolith's DbRecordRepository (PostgreSQL) with DynamoDB operations.
    ///
    /// Single-table design:
    ///   PK = ENTITY#{entityName}  |  SK = RECORD#{recordId}
    ///   GSI1: GSI1PK = ENTITY#{entityName}, GSI1SK = CREATED_ON#{timestamp}
    ///   GSI2: GSI2PK = ENTITY#{entityName}, GSI2SK = X_SEARCH#{searchText}
    ///
    /// All CRM entities (account, contact, address, salutation) share ONE DynamoDB table.
    /// Constructor injection only — no ambient context, no AsyncLocal, no static mutable state.
    /// </summary>
    public class CrmRepository : ICrmRepository
    {
        // DynamoDB single-table key prefixes
        private const string PK_PREFIX = "ENTITY#";
        private const string SK_PREFIX = "RECORD#";
        private const string GSI1_INDEX_NAME = "GSI1";
        private const string GSI1_PK_PREFIX = "ENTITY#";
        private const string GSI1_SK_PREFIX = "CREATED_ON#";
        private const string GSI2_INDEX_NAME = "GSI2";
        private const string GSI2_PK_PREFIX = "ENTITY#";
        private const string GSI2_SK_PREFIX = "X_SEARCH#";

        // DynamoDB key attribute names
        private const string PK_ATTR = "PK";
        private const string SK_ATTR = "SK";
        private const string GSI1_PK_ATTR = "GSI1PK";
        private const string GSI1_SK_ATTR = "GSI1SK";
        private const string GSI2_PK_ATTR = "GSI2PK";
        private const string GSI2_SK_ATTR = "GSI2SK";

        // DynamoDB batch operation limits
        private const int BATCH_WRITE_LIMIT = 25;
        private const int BATCH_GET_LIMIT = 100;
        private const int MAX_RETRY_ATTEMPTS = 5;

        /// <summary>CRM entity name constant for Account. From NextPlugin.20190204 entity creation (ID: 2e22b50f-e444-4b62-a171-076e51246939).</summary>
        public const string AccountEntity = "account";

        /// <summary>CRM entity name constant for Contact. From NextPlugin.20190204 entity creation (ID: 39e1dd9b-827f-464d-95ea-507ade81cbd0).</summary>
        public const string ContactEntity = "contact";

        /// <summary>CRM entity name constant for Address. From NextPlugin.20190204 entity creation.</summary>
        public const string AddressEntity = "address";

        /// <summary>CRM entity name constant for Salutation. From NextPlugin.20190206 entity creation (corrected from misspelled "solutation").</summary>
        public const string SalutationEntity = "salutation";

        private readonly IAmazonDynamoDB _dynamoDbClient;
        private readonly ILogger<CrmRepository> _logger;
        private readonly string _tableName;

        /// <summary>
        /// Initializes a new CrmRepository instance with constructor-injected dependencies.
        /// IAmazonDynamoDB must be configured at the DI level with ServiceURL from
        /// AWS_ENDPOINT_URL environment variable for LocalStack compatibility.
        /// </summary>
        /// <param name="dynamoDbClient">DynamoDB client (DI-configured with endpoint for LocalStack or production AWS).</param>
        /// <param name="logger">Structured JSON logger with correlation-ID support.</param>
        /// <param name="configuration">Configuration provider for DynamoDB table name (SSM via IConfiguration).</param>
        public CrmRepository(IAmazonDynamoDB dynamoDbClient, ILogger<CrmRepository> logger, IConfiguration configuration)
        {
            _dynamoDbClient = dynamoDbClient ?? throw new ArgumentNullException(nameof(dynamoDbClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _tableName = configuration?["DynamoDB:CrmTableName"] ?? "crm-records";
        }

        // =====================================================================
        // CRUD — CreateAsync
        // Source: DbRecordRepository.Create() (lines 87-140)
        // =====================================================================

        /// <inheritdoc />
        public async Task CreateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string entityName, T record, CancellationToken ct = default) where T : class
        {
            if (string.IsNullOrWhiteSpace(entityName))
                throw new ArgumentException("Entity name must not be empty.", nameof(entityName));
            if (record == null)
                throw new ArgumentNullException(nameof(record));

            var item = SerializeToItem(entityName, record);
            var recordId = ExtractRecordId(record);

            // Set composite primary key: PK=ENTITY#{entityName}, SK=RECORD#{recordId}
            item[PK_ATTR] = new AttributeValue { S = PK_PREFIX + entityName };
            item[SK_ATTR] = new AttributeValue { S = SK_PREFIX + recordId.ToString() };

            // Set GSI1 for listing by entity type, sorted by creation time
            var createdOn = ExtractCreatedOn(record);
            item[GSI1_PK_ATTR] = new AttributeValue { S = GSI1_PK_PREFIX + entityName };
            item[GSI1_SK_ATTR] = new AttributeValue { S = GSI1_SK_PREFIX + createdOn.ToString("O") };

            // Set GSI2 for search indexing (x_search field)
            var xSearchValue = ExtractXSearchValue(record);
            if (xSearchValue != null)
            {
                item[GSI2_PK_ATTR] = new AttributeValue { S = GSI2_PK_PREFIX + entityName };
                item[GSI2_SK_ATTR] = new AttributeValue { S = GSI2_SK_PREFIX + xSearchValue.ToLowerInvariant() };
            }

            var request = new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                // Idempotent conditional write — prevent overwriting existing records (AAP §0.8.5)
                ConditionExpression = "attribute_not_exists(PK) AND attribute_not_exists(SK)"
            };

            try
            {
                await _dynamoDbClient.PutItemAsync(request, ct).ConfigureAwait(false);
                _logger.LogInformation("Created {EntityName} record {RecordId} in table {TableName}",
                    entityName, recordId, _tableName);
            }
            catch (ConditionalCheckFailedException)
            {
                _logger.LogWarning("Duplicate create attempt for {EntityName} record {RecordId}", entityName, recordId);
                throw new InvalidOperationException(
                    $"A record with ID '{recordId}' already exists for entity '{entityName}'.");
            }
        }

        // =====================================================================
        // CRUD — GetByIdAsync
        // Source: DbRecordRepository.Find(string entityName, Guid id) (lines 598-603)
        // =====================================================================

        /// <inheritdoc />
        public async Task<T?> GetByIdAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string entityName, Guid recordId, CancellationToken ct = default) where T : class, new()
        {
            if (string.IsNullOrWhiteSpace(entityName))
                throw new ArgumentException("Entity name must not be empty.", nameof(entityName));

            var request = new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    [PK_ATTR] = new AttributeValue { S = PK_PREFIX + entityName },
                    [SK_ATTR] = new AttributeValue { S = SK_PREFIX + recordId.ToString() }
                },
                ConsistentRead = false // Eventually consistent for read performance
            };

            var response = await _dynamoDbClient.GetItemAsync(request, ct).ConfigureAwait(false);

            if (response.Item == null || response.Item.Count == 0)
            {
                _logger.LogInformation("Record {RecordId} not found for entity {EntityName}", recordId, entityName);
                return null;
            }

            _logger.LogInformation("Retrieved {EntityName} record {RecordId}", entityName, recordId);
            return DeserializeFromItem<T>(response.Item);
        }

        // =====================================================================
        // CRUD — UpdateAsync
        // Source: DbRecordRepository.Update() (lines 141-193)
        // =====================================================================

        /// <inheritdoc />
        public async Task UpdateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string entityName, Guid recordId, T record, CancellationToken ct = default) where T : class
        {
            if (string.IsNullOrWhiteSpace(entityName))
                throw new ArgumentException("Entity name must not be empty.", nameof(entityName));
            if (record == null)
                throw new ArgumentNullException(nameof(record));
            if (recordId == Guid.Empty)
                throw new ArgumentException("ID is missing. Cannot update records without ID specified.", nameof(recordId));

            var item = SerializeToItem(entityName, record);

            // Build UpdateExpression with SET for each non-key property
            var setExpressions = new List<string>();
            var expressionAttributeNames = new Dictionary<string, string>();
            var expressionAttributeValues = new Dictionary<string, AttributeValue>();
            int paramIndex = 0;

            foreach (var kvp in item)
            {
                // Skip the primary key and sort key — they cannot be updated
                if (kvp.Key == PK_ATTR || kvp.Key == SK_ATTR)
                    continue;

                var nameAlias = $"#attr{paramIndex}";
                var valueAlias = $":val{paramIndex}";

                expressionAttributeNames[nameAlias] = kvp.Key;
                expressionAttributeValues[valueAlias] = kvp.Value;
                setExpressions.Add($"{nameAlias} = {valueAlias}");
                paramIndex++;
            }

            // Update GSI1 sort key (creation time may not change, but ensure consistency)
            var createdOn = ExtractCreatedOn(record);
            var gsi1SkAlias = $"#attr{paramIndex}";
            var gsi1SkValAlias = $":val{paramIndex}";
            expressionAttributeNames[gsi1SkAlias] = GSI1_SK_ATTR;
            expressionAttributeValues[gsi1SkValAlias] = new AttributeValue { S = GSI1_SK_PREFIX + createdOn.ToString("O") };
            setExpressions.Add($"{gsi1SkAlias} = {gsi1SkValAlias}");
            paramIndex++;

            // Ensure GSI1 PK is set
            var gsi1PkAlias = $"#attr{paramIndex}";
            var gsi1PkValAlias = $":val{paramIndex}";
            expressionAttributeNames[gsi1PkAlias] = GSI1_PK_ATTR;
            expressionAttributeValues[gsi1PkValAlias] = new AttributeValue { S = GSI1_PK_PREFIX + entityName };
            setExpressions.Add($"{gsi1PkAlias} = {gsi1PkValAlias}");
            paramIndex++;

            // Update GSI2 search key if x_search field exists
            var xSearchValue = ExtractXSearchValue(record);
            if (xSearchValue != null)
            {
                var gsi2PkAlias = $"#attr{paramIndex}";
                var gsi2PkValAlias = $":val{paramIndex}";
                expressionAttributeNames[gsi2PkAlias] = GSI2_PK_ATTR;
                expressionAttributeValues[gsi2PkValAlias] = new AttributeValue { S = GSI2_PK_PREFIX + entityName };
                setExpressions.Add($"{gsi2PkAlias} = {gsi2PkValAlias}");
                paramIndex++;

                var gsi2SkAlias = $"#attr{paramIndex}";
                var gsi2SkValAlias = $":val{paramIndex}";
                expressionAttributeNames[gsi2SkAlias] = GSI2_SK_ATTR;
                expressionAttributeValues[gsi2SkValAlias] = new AttributeValue { S = GSI2_SK_PREFIX + xSearchValue.ToLowerInvariant() };
                setExpressions.Add($"{gsi2SkAlias} = {gsi2SkValAlias}");
                paramIndex++;
            }

            if (setExpressions.Count == 0)
            {
                _logger.LogWarning("No attributes to update for {EntityName} record {RecordId}", entityName, recordId);
                return;
            }

            var updateRequest = new UpdateItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    [PK_ATTR] = new AttributeValue { S = PK_PREFIX + entityName },
                    [SK_ATTR] = new AttributeValue { S = SK_PREFIX + recordId.ToString() }
                },
                UpdateExpression = "SET " + string.Join(", ", setExpressions),
                ExpressionAttributeNames = expressionAttributeNames,
                ExpressionAttributeValues = expressionAttributeValues,
                // Conditional expression to ensure record exists (AAP §0.8.5 idempotency)
                ConditionExpression = "attribute_exists(PK) AND attribute_exists(SK)"
            };

            try
            {
                await _dynamoDbClient.UpdateItemAsync(updateRequest, ct).ConfigureAwait(false);
                _logger.LogInformation("Updated {EntityName} record {RecordId}", entityName, recordId);
            }
            catch (ConditionalCheckFailedException)
            {
                _logger.LogWarning("Update failed — record not found: {EntityName} {RecordId}", entityName, recordId);
                throw new InvalidOperationException("Failed to update record.");
            }
        }

        // =====================================================================
        // CRUD — DeleteAsync
        // Source: DbRecordRepository.Delete() (lines 195-204)
        // =====================================================================

        /// <inheritdoc />
        public async Task DeleteAsync(string entityName, Guid recordId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(entityName))
                throw new ArgumentException("Entity name must not be empty.", nameof(entityName));

            // Match source pattern: Find first, then delete (DbRecordRepository lines 199-201)
            var existing = await GetByIdAsync<Dictionary<string, AttributeValue>>(entityName, recordId, ct).ConfigureAwait(false);
            if (existing == null)
                throw new InvalidOperationException("There is no record with such id to update.");

            var deleteRequest = new DeleteItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    [PK_ATTR] = new AttributeValue { S = PK_PREFIX + entityName },
                    [SK_ATTR] = new AttributeValue { S = SK_PREFIX + recordId.ToString() }
                },
                // Safety condition to prevent phantom deletes (AAP §0.8.5)
                ConditionExpression = "attribute_exists(PK)"
            };

            try
            {
                await _dynamoDbClient.DeleteItemAsync(deleteRequest, ct).ConfigureAwait(false);
                _logger.LogInformation("Deleted {EntityName} record {RecordId}", entityName, recordId);
            }
            catch (ConditionalCheckFailedException)
            {
                _logger.LogWarning("Delete condition check failed for {EntityName} {RecordId}", entityName, recordId);
                throw new InvalidOperationException("There is no record with such id to update.");
            }
        }

        // =====================================================================
        // Query — QueryAsync
        // Source: DbRecordRepository.Find(EntityQuery) (lines 605-800+)
        //         and GenerateWhereClause (lines 1167-1560+)
        // =====================================================================

        /// <inheritdoc />
        public async Task<List<T>> QueryAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string entityName, QueryFilter? filter = null, SortOptions? sort = null, PaginationOptions? pagination = null, CancellationToken ct = default) where T : class, new()
        {
            if (string.IsNullOrWhiteSpace(entityName))
                throw new ArgumentException("Entity name must not be empty.", nameof(entityName));

            // Track whether we need client-side regex filtering
            bool hasRegexFilter = ContainsRegexFilter(filter);
            bool hasFtsFilter = ContainsFtsFilter(filter);

            // Build DynamoDB QueryRequest on GSI1 for listing by entity type
            var request = new QueryRequest
            {
                TableName = _tableName,
                IndexName = GSI1_INDEX_NAME,
                KeyConditionExpression = "#gsi1pk = :gsi1pkVal",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#gsi1pk"] = GSI1_PK_ATTR
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":gsi1pkVal"] = new AttributeValue { S = GSI1_PK_PREFIX + entityName }
                },
                ScanIndexForward = true // Default ascending by GSI1SK (CREATED_ON#)
            };

            // Apply sort direction for created_on via GSI1
            if (sort != null && string.Equals(sort.FieldName, "created_on", StringComparison.OrdinalIgnoreCase))
            {
                request.ScanIndexForward = sort.Direction == SortDirection.Ascending;
            }

            // Apply DynamoDB-native filter expressions (excluding Regex and FTS which need client-side handling)
            if (filter != null && !IsFilterEmpty(filter))
            {
                var filterExprNames = new Dictionary<string, string>(request.ExpressionAttributeNames);
                var filterExprValues = new Dictionary<string, AttributeValue>(request.ExpressionAttributeValues);
                int paramCounter = 0;

                var filterExpression = BuildFilterExpression(filter, filterExprNames, filterExprValues, ref paramCounter, excludeRegex: true, excludeFts: hasFtsFilter);

                if (!string.IsNullOrWhiteSpace(filterExpression))
                {
                    request.FilterExpression = filterExpression;
                    request.ExpressionAttributeNames = filterExprNames;
                    request.ExpressionAttributeValues = filterExprValues;
                }
            }

            // Apply pagination limit
            if (pagination?.Limit.HasValue == true && !hasRegexFilter)
            {
                request.Limit = pagination.Limit.Value + (pagination.Skip ?? 0);
            }

            // Apply cursor-based pagination
            if (!string.IsNullOrWhiteSpace(pagination?.ExclusiveStartKey))
            {
                request.ExclusiveStartKey = DeserializeExclusiveStartKey(pagination.ExclusiveStartKey);
            }

            // Execute query with auto-pagination for large result sets
            var allItems = new List<Dictionary<string, AttributeValue>>();
            QueryResponse? response = null;

            do
            {
                if (response?.LastEvaluatedKey?.Count > 0)
                {
                    request.ExclusiveStartKey = response.LastEvaluatedKey;
                }

                response = await _dynamoDbClient.QueryAsync(request, ct).ConfigureAwait(false);
                allItems.AddRange(response.Items);

                // If we have a limit and are not doing client-side regex filtering, stop early
                if (pagination?.Limit.HasValue == true && !hasRegexFilter && !hasFtsFilter)
                {
                    if (allItems.Count >= pagination.Limit.Value + (pagination.Skip ?? 0))
                        break;
                }
            }
            while (response.LastEvaluatedKey?.Count > 0);

            // Deserialize items to typed entities
            var results = allItems.Select(DeserializeFromItem<T>).ToList();

            // Apply client-side regex filtering if needed (DynamoDB does not support regex natively)
            if (hasRegexFilter && filter != null)
            {
                _logger.LogWarning("Applying client-side regex filtering for {EntityName} — performance may be degraded compared to PostgreSQL SIMILAR TO", entityName);
                results = ApplyClientSideRegexFilter(results, filter);
            }

            // Apply client-side FTS filtering (DynamoDB does not have native FTS)
            if (hasFtsFilter && filter != null)
            {
                _logger.LogWarning("Applying approximate FTS filtering for {EntityName} — degraded compared to PostgreSQL to_tsvector/to_tsquery", entityName);
                results = ApplyClientSideFtsFilter(results, filter);
            }

            // Apply skip (DynamoDB doesn't support OFFSET natively)
            if (pagination?.Skip.HasValue == true && pagination.Skip.Value > 0)
            {
                results = results.Skip(pagination.Skip.Value).ToList();
            }

            // Apply in-memory sorting for fields other than created_on
            if (sort != null && !string.Equals(sort.FieldName, "created_on", StringComparison.OrdinalIgnoreCase))
            {
                results = ApplyInMemorySort(results, sort);
            }

            // Apply final limit
            if (pagination?.Limit.HasValue == true)
            {
                results = results.Take(pagination.Limit.Value).ToList();
            }

            _logger.LogInformation("Queried {Count} {EntityName} records", results.Count, entityName);
            return results;
        }

        // =====================================================================
        // Query — CountAsync
        // Source: DbRecordRepository.Count() (lines 271-301)
        // =====================================================================

        /// <inheritdoc />
        public async Task<long> CountAsync(string entityName, QueryFilter? filter = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(entityName))
                throw new ArgumentException("Entity name must not be empty.", nameof(entityName));

            var request = new QueryRequest
            {
                TableName = _tableName,
                IndexName = GSI1_INDEX_NAME,
                KeyConditionExpression = "#gsi1pk = :gsi1pkVal",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#gsi1pk"] = GSI1_PK_ATTR
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":gsi1pkVal"] = new AttributeValue { S = GSI1_PK_PREFIX + entityName }
                },
                Select = Select.COUNT // Only count, no projection (replaces SQL SELECT COUNT(*))
            };

            // Apply filter expressions for count query
            if (filter != null && !IsFilterEmpty(filter))
            {
                var filterExprNames = new Dictionary<string, string>(request.ExpressionAttributeNames);
                var filterExprValues = new Dictionary<string, AttributeValue>(request.ExpressionAttributeValues);
                int paramCounter = 0;

                var filterExpression = BuildFilterExpression(filter, filterExprNames, filterExprValues, ref paramCounter, excludeRegex: true, excludeFts: true);

                if (!string.IsNullOrWhiteSpace(filterExpression))
                {
                    request.FilterExpression = filterExpression;
                    request.ExpressionAttributeNames = filterExprNames;
                    request.ExpressionAttributeValues = filterExprValues;
                }
            }

            long totalCount = 0;
            QueryResponse? response = null;

            do
            {
                if (response?.LastEvaluatedKey?.Count > 0)
                {
                    request.ExclusiveStartKey = response.LastEvaluatedKey;
                }

                response = await _dynamoDbClient.QueryAsync(request, ct).ConfigureAwait(false);
                totalCount += response.Count;
            }
            while (response.LastEvaluatedKey?.Count > 0);

            _logger.LogInformation("Counted {Count} {EntityName} records", totalCount, entityName);
            return totalCount;
        }

        // =====================================================================
        // Search — SearchAsync (GSI2)
        // Source: NextPlugin/Services/SearchService.cs x_search LIKE pattern
        // =====================================================================

        /// <inheritdoc />
        public async Task<List<T>> SearchAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string entityName, string searchText, PaginationOptions? pagination = null, CancellationToken ct = default) where T : class, new()
        {
            if (string.IsNullOrWhiteSpace(entityName))
                throw new ArgumentException("Entity name must not be empty.", nameof(entityName));
            if (string.IsNullOrWhiteSpace(searchText))
                return new List<T>();

            var normalizedSearch = searchText.Trim().ToLowerInvariant();

            // Strategy 1: Try GSI2 prefix-based search first (efficient for prefix matches)
            var request = new QueryRequest
            {
                TableName = _tableName,
                IndexName = GSI2_INDEX_NAME,
                KeyConditionExpression = "#gsi2pk = :gsi2pkVal AND begins_with(#gsi2sk, :gsi2skPrefix)",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#gsi2pk"] = GSI2_PK_ATTR,
                    ["#gsi2sk"] = GSI2_SK_ATTR
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":gsi2pkVal"] = new AttributeValue { S = GSI2_PK_PREFIX + entityName },
                    [":gsi2skPrefix"] = new AttributeValue { S = GSI2_SK_PREFIX + normalizedSearch }
                }
            };

            if (pagination?.Limit.HasValue == true)
            {
                request.Limit = pagination.Limit.Value + (pagination.Skip ?? 0);
            }

            if (!string.IsNullOrWhiteSpace(pagination?.ExclusiveStartKey))
            {
                request.ExclusiveStartKey = DeserializeExclusiveStartKey(pagination.ExclusiveStartKey);
            }

            var allItems = new List<Dictionary<string, AttributeValue>>();
            QueryResponse? response = null;

            do
            {
                if (response?.LastEvaluatedKey?.Count > 0)
                {
                    request.ExclusiveStartKey = response.LastEvaluatedKey;
                }

                response = await _dynamoDbClient.QueryAsync(request, ct).ConfigureAwait(false);
                allItems.AddRange(response.Items);
            }
            while (response.LastEvaluatedKey?.Count > 0);

            // If no results from prefix search, fall back to broader contains search on GSI1
            if (allItems.Count == 0)
            {
                _logger.LogInformation("GSI2 prefix search returned no results for '{SearchText}' on {EntityName}, falling back to contains filter",
                    normalizedSearch, entityName);

                var fallbackRequest = new QueryRequest
                {
                    TableName = _tableName,
                    IndexName = GSI1_INDEX_NAME,
                    KeyConditionExpression = "#gsi1pk = :gsi1pkVal",
                    FilterExpression = "contains(#xsearch, :searchVal)",
                    ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        ["#gsi1pk"] = GSI1_PK_ATTR,
                        ["#xsearch"] = "x_search"
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":gsi1pkVal"] = new AttributeValue { S = GSI1_PK_PREFIX + entityName },
                        [":searchVal"] = new AttributeValue { S = normalizedSearch }
                    }
                };

                if (pagination?.Limit.HasValue == true)
                {
                    fallbackRequest.Limit = pagination.Limit.Value + (pagination.Skip ?? 0);
                }

                QueryResponse? fallbackResponse = null;
                do
                {
                    if (fallbackResponse?.LastEvaluatedKey?.Count > 0)
                    {
                        fallbackRequest.ExclusiveStartKey = fallbackResponse.LastEvaluatedKey;
                    }

                    fallbackResponse = await _dynamoDbClient.QueryAsync(fallbackRequest, ct).ConfigureAwait(false);
                    allItems.AddRange(fallbackResponse.Items);
                }
                while (fallbackResponse.LastEvaluatedKey?.Count > 0);
            }

            var results = allItems.Select(DeserializeFromItem<T>).ToList();

            // Apply skip
            if (pagination?.Skip.HasValue == true && pagination.Skip.Value > 0)
            {
                results = results.Skip(pagination.Skip.Value).ToList();
            }

            // Apply limit
            if (pagination?.Limit.HasValue == true)
            {
                results = results.Take(pagination.Limit.Value).ToList();
            }

            _logger.LogInformation("Search returned {Count} {EntityName} records for '{SearchText}'",
                results.Count, entityName, searchText);
            return results;
        }

        // =====================================================================
        // UpdateSearchFieldAsync
        // Updates the x_search composite index field for a specific record
        // =====================================================================

        /// <inheritdoc />
        public async Task UpdateSearchFieldAsync(string entityName, Guid recordId, string searchFieldValue, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(entityName))
                throw new ArgumentException("Entity name must not be empty.", nameof(entityName));
            if (recordId == Guid.Empty)
                throw new ArgumentException("Record ID must not be empty.", nameof(recordId));

            var normalizedSearch = (searchFieldValue ?? string.Empty).ToLowerInvariant();

            var updateRequest = new UpdateItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    [PK_ATTR] = new AttributeValue { S = PK_PREFIX + entityName },
                    [SK_ATTR] = new AttributeValue { S = SK_PREFIX + recordId.ToString() }
                },
                UpdateExpression = "SET #xsearch = :xsearchVal, #gsi2pk = :gsi2pkVal, #gsi2sk = :gsi2skVal",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#xsearch"] = "x_search",
                    ["#gsi2pk"] = GSI2_PK_ATTR,
                    ["#gsi2sk"] = GSI2_SK_ATTR
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":xsearchVal"] = new AttributeValue { S = searchFieldValue ?? string.Empty },
                    [":gsi2pkVal"] = new AttributeValue { S = GSI2_PK_PREFIX + entityName },
                    [":gsi2skVal"] = new AttributeValue { S = GSI2_SK_PREFIX + normalizedSearch }
                },
                ConditionExpression = "attribute_exists(PK) AND attribute_exists(SK)"
            };

            try
            {
                await _dynamoDbClient.UpdateItemAsync(updateRequest, ct).ConfigureAwait(false);
                _logger.LogInformation("Updated x_search for {EntityName} record {RecordId}", entityName, recordId);
            }
            catch (ConditionalCheckFailedException)
            {
                _logger.LogWarning("UpdateSearchField failed — record not found: {EntityName} {RecordId}", entityName, recordId);
                throw new InvalidOperationException("Failed to update record.");
            }
        }

        // =====================================================================
        // Batch — BatchCreateAsync
        // Uses DynamoDB BatchWriteItem (max 25 items per request)
        // =====================================================================

        /// <inheritdoc />
        public async Task BatchCreateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string entityName, IEnumerable<T> records, CancellationToken ct = default) where T : class
        {
            if (string.IsNullOrWhiteSpace(entityName))
                throw new ArgumentException("Entity name must not be empty.", nameof(entityName));
            if (records == null)
                throw new ArgumentNullException(nameof(records));

            var recordList = records.ToList();
            if (recordList.Count == 0)
                return;

            // Chunk into batches of 25 (DynamoDB BatchWriteItem hard limit)
            var batches = recordList.Chunk(BATCH_WRITE_LIMIT);
            int totalProcessed = 0;

            foreach (var batch in batches)
            {
                var writeRequests = new List<WriteRequest>();

                foreach (var record in batch)
                {
                    var item = SerializeToItem(entityName, record);
                    var recordId = ExtractRecordId(record);
                    var createdOn = ExtractCreatedOn(record);

                    // Set composite primary key
                    item[PK_ATTR] = new AttributeValue { S = PK_PREFIX + entityName };
                    item[SK_ATTR] = new AttributeValue { S = SK_PREFIX + recordId.ToString() };

                    // Set GSI1 attributes
                    item[GSI1_PK_ATTR] = new AttributeValue { S = GSI1_PK_PREFIX + entityName };
                    item[GSI1_SK_ATTR] = new AttributeValue { S = GSI1_SK_PREFIX + createdOn.ToString("O") };

                    // Set GSI2 attributes for search indexing
                    var xSearchValue = ExtractXSearchValue(record);
                    if (xSearchValue != null)
                    {
                        item[GSI2_PK_ATTR] = new AttributeValue { S = GSI2_PK_PREFIX + entityName };
                        item[GSI2_SK_ATTR] = new AttributeValue { S = GSI2_SK_PREFIX + xSearchValue.ToLowerInvariant() };
                    }

                    writeRequests.Add(new WriteRequest
                    {
                        PutRequest = new PutRequest { Item = item }
                    });
                }

                var batchRequest = new BatchWriteItemRequest
                {
                    RequestItems = new Dictionary<string, List<WriteRequest>>
                    {
                        [_tableName] = writeRequests
                    }
                };

                // Execute with exponential backoff retry for UnprocessedItems
                int retryAttempt = 0;
                BatchWriteItemResponse? batchResponse;

                do
                {
                    batchResponse = await _dynamoDbClient.BatchWriteItemAsync(batchRequest, ct).ConfigureAwait(false);

                    if (batchResponse.UnprocessedItems?.Count > 0)
                    {
                        retryAttempt++;
                        if (retryAttempt > MAX_RETRY_ATTEMPTS)
                        {
                            _logger.LogError("BatchCreate exceeded max retries for {EntityName}. {UnprocessedCount} items remaining.",
                                entityName, batchResponse.UnprocessedItems[_tableName].Count);
                            throw new InvalidOperationException(
                                $"Failed to process all batch items after {MAX_RETRY_ATTEMPTS} retries.");
                        }

                        // Exponential backoff: 100ms, 200ms, 400ms, 800ms, 1600ms
                        var delay = (int)(100 * Math.Pow(2, retryAttempt - 1));
                        _logger.LogWarning("BatchCreate retry {Attempt} for {EntityName} — {UnprocessedCount} unprocessed items, waiting {Delay}ms",
                            retryAttempt, entityName, batchResponse.UnprocessedItems[_tableName].Count, delay);
                        await Task.Delay(delay, ct).ConfigureAwait(false);

                        batchRequest.RequestItems = batchResponse.UnprocessedItems;
                    }
                }
                while (batchResponse.UnprocessedItems?.Count > 0);

                totalProcessed += batch.Length;
                _logger.LogInformation("BatchCreate progress: {Processed}/{Total} {EntityName} records",
                    totalProcessed, recordList.Count, entityName);
            }

            _logger.LogInformation("BatchCreate completed: {Total} {EntityName} records created", recordList.Count, entityName);
        }

        // =====================================================================
        // Batch — BatchGetAsync
        // Uses DynamoDB BatchGetItem (max 100 items per request)
        // =====================================================================

        /// <inheritdoc />
        public async Task<List<T>> BatchGetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string entityName, IEnumerable<Guid> recordIds, CancellationToken ct = default) where T : class, new()
        {
            if (string.IsNullOrWhiteSpace(entityName))
                throw new ArgumentException("Entity name must not be empty.", nameof(entityName));
            if (recordIds == null)
                throw new ArgumentNullException(nameof(recordIds));

            var idList = recordIds.ToList();
            if (idList.Count == 0)
                return new List<T>();

            var allResults = new List<T>();

            // Chunk into batches of 100 (DynamoDB BatchGetItem hard limit)
            var batches = idList.Chunk(BATCH_GET_LIMIT);

            foreach (var batch in batches)
            {
                var keys = batch.Select(id => new Dictionary<string, AttributeValue>
                {
                    [PK_ATTR] = new AttributeValue { S = PK_PREFIX + entityName },
                    [SK_ATTR] = new AttributeValue { S = SK_PREFIX + id.ToString() }
                }).ToList();

                var batchRequest = new BatchGetItemRequest
                {
                    RequestItems = new Dictionary<string, KeysAndAttributes>
                    {
                        [_tableName] = new KeysAndAttributes
                        {
                            Keys = keys,
                            ConsistentRead = false
                        }
                    }
                };

                // Execute with exponential backoff retry for UnprocessedKeys
                int retryAttempt = 0;
                BatchGetItemResponse? batchResponse;

                do
                {
                    batchResponse = await _dynamoDbClient.BatchGetItemAsync(batchRequest, ct).ConfigureAwait(false);

                    if (batchResponse.Responses.ContainsKey(_tableName))
                    {
                        foreach (var item in batchResponse.Responses[_tableName])
                        {
                            allResults.Add(DeserializeFromItem<T>(item));
                        }
                    }

                    if (batchResponse.UnprocessedKeys?.Count > 0)
                    {
                        retryAttempt++;
                        if (retryAttempt > MAX_RETRY_ATTEMPTS)
                        {
                            _logger.LogError("BatchGet exceeded max retries for {EntityName}. Unprocessed keys remaining.", entityName);
                            throw new InvalidOperationException(
                                $"Failed to process all batch get items after {MAX_RETRY_ATTEMPTS} retries.");
                        }

                        var delay = (int)(100 * Math.Pow(2, retryAttempt - 1));
                        _logger.LogWarning("BatchGet retry {Attempt} for {EntityName}, waiting {Delay}ms",
                            retryAttempt, entityName, delay);
                        await Task.Delay(delay, ct).ConfigureAwait(false);

                        batchRequest.RequestItems = batchResponse.UnprocessedKeys;
                    }
                }
                while (batchResponse.UnprocessedKeys?.Count > 0);
            }

            _logger.LogInformation("BatchGet returned {Count} {EntityName} records", allResults.Count, entityName);
            return allResults;
        }

        // =====================================================================
        // Private Helpers — Serialization / Deserialization
        // Replaces DbTypeConverter.ConvertToDatabaseType from source DBTypeConverter.cs
        // =====================================================================

        /// <summary>
        /// Serializes a typed entity record to a DynamoDB item (Dictionary of AttributeValue).
        /// Uses reflection to read [JsonPropertyName] attributes for DynamoDB attribute naming.
        /// Type mapping: Guid→S, string→S, DateTime→S(ISO8601), decimal/int→N, bool→BOOL, List→L.
        /// </summary>
        private Dictionary<string, AttributeValue> SerializeToItem<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string entityName, T record) where T : class
        {
            var item = new Dictionary<string, AttributeValue>();
            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var prop in properties)
            {
                // Skip static members (EntityId, DefaultSalutationId, etc.)
                if (prop.GetGetMethod()?.IsStatic == true)
                    continue;

                // Use [JsonPropertyName] attribute for DynamoDB attribute name, fallback to property name
                var jsonAttr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
                var attrName = jsonAttr?.Name ?? prop.Name.ToLowerInvariant();

                var value = prop.GetValue(record);

                var attributeValue = ConvertToAttributeValue(value, prop.PropertyType);
                if (attributeValue != null)
                {
                    item[attrName] = attributeValue;
                }
            }

            return item;
        }

        /// <summary>
        /// Deserializes a DynamoDB item to a typed entity record.
        /// Uses reflection to read [JsonPropertyName] attributes and maps DynamoDB
        /// attribute types back to .NET types. Handles missing attributes gracefully.
        /// </summary>
        private T DeserializeFromItem<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(Dictionary<string, AttributeValue> item) where T : class, new()
        {
            var result = new T();
            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var prop in properties)
            {
                // Skip static members and read-only properties
                if (prop.GetGetMethod()?.IsStatic == true || prop.GetSetMethod() == null)
                    continue;

                var jsonAttr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
                var attrName = jsonAttr?.Name ?? prop.Name.ToLowerInvariant();

                if (!item.TryGetValue(attrName, out var attributeValue))
                    continue;

                var convertedValue = ConvertFromAttributeValue(attributeValue, prop.PropertyType);
                if (convertedValue != null)
                {
                    prop.SetValue(result, convertedValue);
                }
            }

            return result;
        }

        /// <summary>
        /// Converts a .NET value to a DynamoDB AttributeValue.
        /// Replaces DbTypeConverter.ConvertToDatabaseType from source DBTypeConverter.cs.
        /// </summary>
        private static AttributeValue? ConvertToAttributeValue(object? value, Type propertyType)
        {
            if (value == null)
            {
                return new AttributeValue { NULL = true };
            }

            var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

            if (underlyingType == typeof(Guid))
            {
                var guidVal = (Guid)value;
                return new AttributeValue { S = guidVal.ToString() };
            }

            if (underlyingType == typeof(string))
            {
                var strVal = (string)value;
                // DynamoDB does not allow empty strings for key attributes,
                // but allows them for non-key string attributes
                return new AttributeValue { S = strVal };
            }

            if (underlyingType == typeof(DateTime))
            {
                var dtVal = (DateTime)value;
                return new AttributeValue { S = dtVal.ToString("O") };
            }

            if (underlyingType == typeof(decimal))
            {
                var decVal = (decimal)value;
                return new AttributeValue { N = decVal.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            }

            if (underlyingType == typeof(int))
            {
                var intVal = (int)value;
                return new AttributeValue { N = intVal.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            }

            if (underlyingType == typeof(long))
            {
                var longVal = (long)value;
                return new AttributeValue { N = longVal.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            }

            if (underlyingType == typeof(double))
            {
                var dblVal = (double)value;
                return new AttributeValue { N = dblVal.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            }

            if (underlyingType == typeof(bool))
            {
                var boolVal = (bool)value;
                return new AttributeValue { BOOL = boolVal };
            }

            // Handle List<string> for MultiSelect fields
            if (value is IList<string> stringList)
            {
                return new AttributeValue
                {
                    L = stringList.Select(s => new AttributeValue { S = s }).ToList()
                };
            }

            // Handle generic IEnumerable<string>
            if (value is IEnumerable<string> stringEnumerable)
            {
                return new AttributeValue
                {
                    L = stringEnumerable.Select(s => new AttributeValue { S = s }).ToList()
                };
            }

            // Fallback: convert to string representation (AOT-safe, avoids IL2026/IL3050)
            // All CRM-relevant types (Guid, string, DateTime, decimal, int, long, double, bool, List<string>)
            // are handled above. This fallback is for unexpected future types.
            var strRepr = value.ToString() ?? string.Empty;
            return new AttributeValue { S = strRepr };
        }

        /// <summary>
        /// Converts a DynamoDB AttributeValue back to a .NET value of the specified target type.
        /// Handles: S→string/Guid/DateTime, N→decimal/int/long/double, BOOL→bool, L→List&lt;string&gt;.
        /// </summary>
        private static object? ConvertFromAttributeValue(AttributeValue attrValue, Type targetType)
        {
            if (attrValue.NULL)
                return null;

            var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

            // String attribute (S)
            if (attrValue.S != null)
            {
                if (underlyingType == typeof(Guid))
                {
                    return Guid.TryParse(attrValue.S, out var guidResult) ? guidResult : Guid.Empty;
                }

                if (underlyingType == typeof(DateTime))
                {
                    return DateTime.TryParse(attrValue.S, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dtResult)
                        ? dtResult
                        : DateTime.MinValue;
                }

                if (underlyingType == typeof(string))
                {
                    return attrValue.S;
                }
            }

            // Number attribute (N)
            if (attrValue.N != null)
            {
                if (underlyingType == typeof(decimal))
                {
                    return decimal.TryParse(attrValue.N, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var decResult) ? decResult : 0m;
                }

                if (underlyingType == typeof(int))
                {
                    return int.TryParse(attrValue.N, out var intResult) ? intResult : 0;
                }

                if (underlyingType == typeof(long))
                {
                    return long.TryParse(attrValue.N, out var longResult) ? longResult : 0L;
                }

                if (underlyingType == typeof(double))
                {
                    return double.TryParse(attrValue.N, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dblResult) ? dblResult : 0.0;
                }
            }

            // Boolean attribute (BOOL)
            if (underlyingType == typeof(bool))
            {
                return attrValue.BOOL;
            }

            // List attribute (L) → List<string>
            if (attrValue.L != null && attrValue.L.Count > 0)
            {
                if (underlyingType == typeof(List<string>) || underlyingType == typeof(IList<string>))
                {
                    return attrValue.L.Where(v => v.S != null).Select(v => v.S).ToList();
                }
            }

            return null;
        }

        // =====================================================================
        // Private Helpers — Filter Expression Builder
        // Replaces DbRecordRepository.GenerateWhereClause (lines 1167-1560+)
        // =====================================================================

        /// <summary>
        /// Recursively builds a DynamoDB FilterExpression from a QueryFilter tree.
        /// Mirrors the source GenerateWhereClause recursive pattern (lines 1521-1560).
        /// Populates ExpressionAttributeNames and ExpressionAttributeValues dictionaries.
        /// </summary>
        private string BuildFilterExpression(
            QueryFilter filter,
            Dictionary<string, string> expressionAttributeNames,
            Dictionary<string, AttributeValue> expressionAttributeValues,
            ref int paramCounter,
            bool excludeRegex = false,
            bool excludeFts = false)
        {
            // Handle group filter (SubFilters with AND/OR logic)
            if (filter.SubFilters != null && filter.SubFilters.Count > 0)
            {
                var subExpressions = new List<string>();

                foreach (var subFilter in filter.SubFilters)
                {
                    var subExpr = BuildFilterExpression(subFilter, expressionAttributeNames, expressionAttributeValues, ref paramCounter, excludeRegex, excludeFts);
                    if (!string.IsNullOrWhiteSpace(subExpr))
                    {
                        subExpressions.Add(subExpr);
                    }
                }

                if (subExpressions.Count == 0)
                    return string.Empty;

                if (subExpressions.Count == 1)
                    return subExpressions[0];

                var connective = filter.Logic == FilterLogic.Or ? " OR " : " AND ";
                return "(" + string.Join(connective, subExpressions) + ")";
            }

            // Handle leaf filter (FieldName + Operator + Value)
            if (string.IsNullOrWhiteSpace(filter.FieldName))
                return string.Empty;

            // Skip Regex filters in DynamoDB expression (handled client-side)
            if (excludeRegex && filter.Operator == FilterOperator.Regex)
                return string.Empty;

            // Skip FTS filters in DynamoDB expression (handled client-side)
            if (excludeFts && filter.Operator == FilterOperator.Fts)
                return string.Empty;

            var fieldAlias = $"#f{paramCounter}";
            var valueAlias = $":v{paramCounter}";
            paramCounter++;

            expressionAttributeNames[fieldAlias] = filter.FieldName;

            string expression;

            switch (filter.Operator)
            {
                case FilterOperator.Equal:
                    expressionAttributeValues[valueAlias] = CreateFilterAttributeValue(filter.Value);
                    expression = $"{fieldAlias} = {valueAlias}";
                    break;

                case FilterOperator.NotEqual:
                    expressionAttributeValues[valueAlias] = CreateFilterAttributeValue(filter.Value);
                    expression = $"{fieldAlias} <> {valueAlias}";
                    break;

                case FilterOperator.LessThan:
                    expressionAttributeValues[valueAlias] = CreateFilterAttributeValue(filter.Value);
                    expression = $"{fieldAlias} < {valueAlias}";
                    break;

                case FilterOperator.LessThanOrEqual:
                    expressionAttributeValues[valueAlias] = CreateFilterAttributeValue(filter.Value);
                    expression = $"{fieldAlias} <= {valueAlias}";
                    break;

                case FilterOperator.GreaterThan:
                    expressionAttributeValues[valueAlias] = CreateFilterAttributeValue(filter.Value);
                    expression = $"{fieldAlias} > {valueAlias}";
                    break;

                case FilterOperator.GreaterThanOrEqual:
                    expressionAttributeValues[valueAlias] = CreateFilterAttributeValue(filter.Value);
                    expression = $"{fieldAlias} >= {valueAlias}";
                    break;

                case FilterOperator.Contains:
                    expressionAttributeValues[valueAlias] = CreateFilterAttributeValue(filter.Value);
                    expression = $"contains({fieldAlias}, {valueAlias})";
                    break;

                case FilterOperator.StartsWith:
                    expressionAttributeValues[valueAlias] = CreateFilterAttributeValue(filter.Value);
                    expression = $"begins_with({fieldAlias}, {valueAlias})";
                    break;

                case FilterOperator.Fts:
                    // FTS approximation: use contains on x_search field
                    expressionAttributeValues[valueAlias] = CreateFilterAttributeValue(filter.Value);
                    expression = $"contains({fieldAlias}, {valueAlias})";
                    break;

                case FilterOperator.Regex:
                    // Regex cannot be expressed in DynamoDB — should be excluded
                    return string.Empty;

                default:
                    return string.Empty;
            }

            return expression;
        }

        /// <summary>
        /// Creates a DynamoDB AttributeValue from a filter value object.
        /// Handles string, Guid, DateTime, numeric, and boolean filter values.
        /// </summary>
        private static AttributeValue CreateFilterAttributeValue(object? value)
        {
            if (value == null)
                return new AttributeValue { NULL = true };

            if (value is string strVal)
                return new AttributeValue { S = strVal };

            if (value is Guid guidVal)
                return new AttributeValue { S = guidVal.ToString() };

            if (value is DateTime dtVal)
                return new AttributeValue { S = dtVal.ToString("O") };

            if (value is decimal decVal)
                return new AttributeValue { N = decVal.ToString(System.Globalization.CultureInfo.InvariantCulture) };

            if (value is int intVal)
                return new AttributeValue { N = intVal.ToString(System.Globalization.CultureInfo.InvariantCulture) };

            if (value is long longVal)
                return new AttributeValue { N = longVal.ToString(System.Globalization.CultureInfo.InvariantCulture) };

            if (value is double dblVal)
                return new AttributeValue { N = dblVal.ToString(System.Globalization.CultureInfo.InvariantCulture) };

            if (value is bool boolVal)
                return new AttributeValue { BOOL = boolVal };

            // Fallback: treat as string
            return new AttributeValue { S = value.ToString() ?? string.Empty };
        }

        // =====================================================================
        // Private Helpers — Sorting, Filtering, and Utility Methods
        // =====================================================================

        /// <summary>
        /// Applies in-memory sorting using LINQ for fields other than created_on.
        /// DynamoDB can only sort by sort key within a partition; all other field sorts
        /// must be applied in-memory. This is documented as a limitation vs. PostgreSQL ORDER BY.
        /// </summary>
        private List<T> ApplyInMemorySort<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(List<T> items, SortOptions sort) where T : class, new()
        {
            if (items.Count == 0 || string.IsNullOrWhiteSpace(sort.FieldName))
                return items;

            // Find the property matching the sort field name via [JsonPropertyName]
            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo? sortProperty = null;

            foreach (var prop in properties)
            {
                var jsonAttr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
                var attrName = jsonAttr?.Name ?? prop.Name.ToLowerInvariant();
                if (string.Equals(attrName, sort.FieldName, StringComparison.OrdinalIgnoreCase))
                {
                    sortProperty = prop;
                    break;
                }
            }

            if (sortProperty == null)
            {
                _logger.LogWarning("Sort field '{FieldName}' not found on type {TypeName}. Skipping sort.",
                    sort.FieldName, typeof(T).Name);
                return items;
            }

            return sort.Direction == SortDirection.Ascending
                ? items.OrderBy(x => sortProperty.GetValue(x) as IComparable).ToList()
                : items.OrderByDescending(x => sortProperty.GetValue(x) as IComparable).ToList();
        }

        /// <summary>
        /// Applies client-side regex filtering for QueryFilter nodes with FilterOperator.Regex.
        /// DynamoDB does not natively support regex filter expressions.
        /// Replaces PostgreSQL SIMILAR TO from source GenerateWhereClause.
        /// </summary>
        private List<T> ApplyClientSideRegexFilter<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(List<T> items, QueryFilter filter) where T : class, new()
        {
            var regexFilters = CollectFilters(filter, FilterOperator.Regex);
            if (regexFilters.Count == 0)
                return items;

            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var regexFilter in regexFilters)
            {
                if (string.IsNullOrWhiteSpace(regexFilter.FieldName) || regexFilter.Value == null)
                    continue;

                var pattern = regexFilter.Value.ToString() ?? string.Empty;
                PropertyInfo? matchProp = null;

                foreach (var prop in properties)
                {
                    var jsonAttr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
                    var attrName = jsonAttr?.Name ?? prop.Name.ToLowerInvariant();
                    if (string.Equals(attrName, regexFilter.FieldName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchProp = prop;
                        break;
                    }
                }

                if (matchProp == null)
                    continue;

                try
                {
                    var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(5));
                    items = items.Where(item =>
                    {
                        var val = matchProp.GetValue(item)?.ToString();
                        return val != null && regex.IsMatch(val);
                    }).ToList();
                }
                catch (RegexParseException ex)
                {
                    _logger.LogWarning("Invalid regex pattern '{Pattern}' for field '{Field}': {Message}",
                        pattern, regexFilter.FieldName, ex.Message);
                }
            }

            return items;
        }

        /// <summary>
        /// Applies client-side FTS (full-text search) filtering using contains on x_search field.
        /// DynamoDB does not have native FTS; this is an approximation of PostgreSQL to_tsvector/to_tsquery.
        /// </summary>
        private List<T> ApplyClientSideFtsFilter<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(List<T> items, QueryFilter filter) where T : class, new()
        {
            var ftsFilters = CollectFilters(filter, FilterOperator.Fts);
            if (ftsFilters.Count == 0)
                return items;

            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var ftsFilter in ftsFilters)
            {
                if (ftsFilter.Value == null)
                    continue;

                var searchTerms = ftsFilter.Value.ToString()?.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                if (searchTerms.Length == 0)
                    continue;

                // Find the x_search property on the target type
                PropertyInfo? xSearchProp = null;
                foreach (var prop in properties)
                {
                    var jsonAttr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
                    var attrName = jsonAttr?.Name ?? prop.Name.ToLowerInvariant();
                    if (attrName == "x_search")
                    {
                        xSearchProp = prop;
                        break;
                    }
                }

                // Fallback to the field specified in the filter
                if (xSearchProp == null && !string.IsNullOrWhiteSpace(ftsFilter.FieldName))
                {
                    foreach (var prop in properties)
                    {
                        var jsonAttr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
                        var attrName = jsonAttr?.Name ?? prop.Name.ToLowerInvariant();
                        if (string.Equals(attrName, ftsFilter.FieldName, StringComparison.OrdinalIgnoreCase))
                        {
                            xSearchProp = prop;
                            break;
                        }
                    }
                }

                if (xSearchProp == null)
                    continue;

                items = items.Where(item =>
                {
                    var fieldValue = xSearchProp.GetValue(item)?.ToString()?.ToLowerInvariant();
                    if (string.IsNullOrEmpty(fieldValue))
                        return false;

                    // All search terms must be present (AND semantics)
                    return searchTerms.All(term => fieldValue.Contains(term));
                }).ToList();
            }

            return items;
        }

        /// <summary>
        /// Collects all leaf filter nodes with the specified operator from the filter tree.
        /// </summary>
        private static List<QueryFilter> CollectFilters(QueryFilter filter, FilterOperator targetOp)
        {
            var result = new List<QueryFilter>();

            if (filter.SubFilters != null && filter.SubFilters.Count > 0)
            {
                foreach (var sub in filter.SubFilters)
                {
                    result.AddRange(CollectFilters(sub, targetOp));
                }
            }
            else if (filter.Operator == targetOp)
            {
                result.Add(filter);
            }

            return result;
        }

        /// <summary>
        /// Checks if the filter tree contains any Regex-type filters (requiring client-side handling).
        /// </summary>
        private static bool ContainsRegexFilter(QueryFilter? filter)
        {
            if (filter == null) return false;
            if (filter.Operator == FilterOperator.Regex && filter.SubFilters == null)
                return true;
            if (filter.SubFilters != null)
                return filter.SubFilters.Any(ContainsRegexFilter);
            return false;
        }

        /// <summary>
        /// Checks if the filter tree contains any FTS-type filters (requiring client-side handling).
        /// </summary>
        private static bool ContainsFtsFilter(QueryFilter? filter)
        {
            if (filter == null) return false;
            if (filter.Operator == FilterOperator.Fts && filter.SubFilters == null)
                return true;
            if (filter.SubFilters != null)
                return filter.SubFilters.Any(ContainsFtsFilter);
            return false;
        }

        /// <summary>
        /// Checks if the given QueryFilter is effectively empty (no meaningful criteria).
        /// </summary>
        private static bool IsFilterEmpty(QueryFilter filter)
        {
            if (filter.SubFilters != null && filter.SubFilters.Count > 0)
                return filter.SubFilters.All(IsFilterEmpty);

            return string.IsNullOrWhiteSpace(filter.FieldName);
        }

        // =====================================================================
        // Private Helpers — Record property extraction
        // =====================================================================

        /// <summary>
        /// Extracts the Id property value from a record using reflection.
        /// Looks for a property named "Id" with [JsonPropertyName("id")] attribute.
        /// </summary>
        private static Guid ExtractRecordId<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(T record) where T : class
        {
            var idProp = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p =>
                {
                    var jsonAttr = p.GetCustomAttribute<JsonPropertyNameAttribute>();
                    return (jsonAttr?.Name == "id") || string.Equals(p.Name, "Id", StringComparison.OrdinalIgnoreCase);
                });

            if (idProp == null)
                throw new InvalidOperationException($"Type {typeof(T).Name} does not have an 'Id' property.");

            var value = idProp.GetValue(record);
            if (value is Guid guidValue)
                return guidValue;

            throw new InvalidOperationException($"The 'Id' property on {typeof(T).Name} is not of type Guid.");
        }

        /// <summary>
        /// Extracts the CreatedOn property value from a record using reflection.
        /// Falls back to DateTime.UtcNow if property is not found or has default value.
        /// </summary>
        private static DateTime ExtractCreatedOn<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(T record) where T : class
        {
            var createdOnProp = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p =>
                {
                    var jsonAttr = p.GetCustomAttribute<JsonPropertyNameAttribute>();
                    return (jsonAttr?.Name == "created_on") || string.Equals(p.Name, "CreatedOn", StringComparison.OrdinalIgnoreCase);
                });

            if (createdOnProp != null)
            {
                var value = createdOnProp.GetValue(record);
                if (value is DateTime dtValue && dtValue != default)
                    return dtValue;
            }

            return DateTime.UtcNow;
        }

        /// <summary>
        /// Extracts the XSearch (x_search) property value from a record using reflection.
        /// Returns null if the property does not exist on the type (e.g., for address/salutation entities).
        /// </summary>
        private static string? ExtractXSearchValue<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(T record) where T : class
        {
            var xSearchProp = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p =>
                {
                    var jsonAttr = p.GetCustomAttribute<JsonPropertyNameAttribute>();
                    return (jsonAttr?.Name == "x_search") || string.Equals(p.Name, "XSearch", StringComparison.OrdinalIgnoreCase);
                });

            return xSearchProp?.GetValue(record) as string;
        }

        /// <summary>
        /// Serializes a DynamoDB LastEvaluatedKey to a Base64-encoded JSON string for pagination tokens.
        /// Uses source-generated JSON context for AOT/trimming safety (avoids IL2026/IL3050).
        /// </summary>
        private static string SerializeExclusiveStartKey(Dictionary<string, AttributeValue> lastEvaluatedKey)
        {
            var keyMap = new Dictionary<string, string>();
            foreach (var kvp in lastEvaluatedKey)
            {
                if (kvp.Value.S != null)
                    keyMap[kvp.Key] = kvp.Value.S;
                else if (kvp.Value.N != null)
                    keyMap[kvp.Key] = kvp.Value.N;
            }

            var json = JsonSerializer.Serialize(keyMap, CrmPaginationJsonContext.Default.DictionaryStringString);
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        }

        /// <summary>
        /// Deserializes a Base64-encoded JSON pagination token back to DynamoDB ExclusiveStartKey.
        /// Uses source-generated JSON context for AOT/trimming safety (avoids IL2026/IL3050).
        /// </summary>
        private static Dictionary<string, AttributeValue> DeserializeExclusiveStartKey(string token)
        {
            try
            {
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token));
                var keyMap = JsonSerializer.Deserialize(json, CrmPaginationJsonContext.Default.DictionaryStringString)
                    ?? new Dictionary<string, string>();

                var result = new Dictionary<string, AttributeValue>();
                foreach (var kvp in keyMap)
                {
                    result[kvp.Key] = new AttributeValue { S = kvp.Value };
                }

                return result;
            }
            catch (Exception)
            {
                return new Dictionary<string, AttributeValue>();
            }
        }
    }

    /// <summary>
    /// Source-generated JSON serializer context for AOT/trimming-safe pagination token
    /// serialization/deserialization. Generates optimized code at compile time for
    /// Dictionary&lt;string, string&gt; used in DynamoDB ExclusiveStartKey encoding.
    /// </summary>
    [JsonSerializable(typeof(Dictionary<string, string>))]
    internal partial class CrmPaginationJsonContext : JsonSerializerContext
    {
    }
}
