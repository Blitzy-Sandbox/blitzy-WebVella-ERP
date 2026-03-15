// =============================================================================
// SearchService.cs — CRM Search Index Regeneration Service
//
// Complete rewrite of WebVella.Erp.Plugins.Next/Services/SearchService.cs for
// the CRM bounded-context microservice. Generates and persists a denormalized
// full-text search index string (x_search) on CRM entity records (account,
// contact) by formatting field values according to field-type-specific rules.
//
// Namespace: WebVellaErp.Crm.Services
// Source:    WebVella.Erp.Plugins.Next/Services/SearchService.cs
//            WebVella.Erp.Plugins.Next/Configuration.cs
//            WebVella.Erp/Api/Definitions.cs
//
// Key differences from monolith:
//   - Async/await (replacing synchronous RegenSearchField)
//   - DI-injected ICrmRepository (replacing new RecordManager/EntityManager)
//   - DynamoDB queries (replacing EQL + PostgreSQL)
//   - Locally owned entity/relation metadata (replacing runtime EntityManager reads)
//   - Structured ILogger (replacing silent operation)
//   - CancellationToken on all async paths (Lambda graceful shutdown)
//   - No BaseService inheritance
//   - No Newtonsoft.Json, no EQL, no Npgsql
// =============================================================================

using System.Globalization;
using Microsoft.Extensions.Logging;
using WebVellaErp.Crm.DataAccess;
using WebVellaErp.Crm.Models;

namespace WebVellaErp.Crm.Services;

// =============================================================================
// Enum: CurrencySymbolPlacement
// Matches source WebVella.Erp/Api/Definitions.cs lines 58-63
// =============================================================================

/// <summary>
/// Specifies where the currency symbol is placed relative to the numeric amount.
/// Maps to the monolith's CurrencySymbolPlacement enum from Definitions.cs.
/// </summary>
public enum CurrencySymbolPlacement
{
    /// <summary>Symbol placed before the amount (e.g., "$100.00").</summary>
    Before = 1,

    /// <summary>Symbol placed after the amount (e.g., "100.00€").</summary>
    After = 2
}

// =============================================================================
// Enum: CrmFieldType
// Subset of WebVella.Erp/Api/Definitions.cs FieldType — types used by CRM entities
// =============================================================================

/// <summary>
/// Enumerates the field types used by CRM bounded-context entities.
/// This is a targeted subset of the full monolith FieldType enum, covering
/// only types that appear on account, contact, address, and salutation entities.
/// Used by <see cref="GetStringValue"/> for field-type-specific formatting.
/// </summary>
public enum CrmFieldType
{
    Text,
    Email,
    Phone,
    Url,
    MultiLineText,
    AutoNumber,
    Currency,
    Date,
    DateTime,
    Number,
    Percent,
    Select,
    MultiSelect,
    Password,
    Guid,
    Image,
    Boolean
}

// =============================================================================
// Class: SelectOption
// Matches source WebVella.Erp SelectField.SelectOption (SelectField.cs lines 33-67)
// =============================================================================

/// <summary>
/// Represents a single option in a Select or MultiSelect field.
/// Used for resolving display labels from stored option values during
/// search index generation (matching source GetStringValue lines 263-278).
/// </summary>
public class SelectOption
{
    /// <summary>The stored value (e.g., "1" for Company).</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>The display label (e.g., "Company").</summary>
    public string Label { get; set; } = string.Empty;
}

// =============================================================================
// Class: CurrencyInfo
// Matches source WebVella.Erp/Api/Definitions.cs CurrencyType (lines 65-89)
// =============================================================================

/// <summary>
/// Currency metadata for formatting CurrencyField values in the search index.
/// Maps to the monolith's CurrencyType class from Definitions.cs.
/// </summary>
public class CurrencyInfo
{
    /// <summary>ISO 4217 currency code (e.g., "USD").</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Native currency symbol (e.g., "$").</summary>
    public string SymbolNative { get; set; } = string.Empty;

    /// <summary>Number of decimal digits (e.g., 2 for USD).</summary>
    public int DecimalDigits { get; set; }

    /// <summary>Whether the symbol is placed before or after the amount.</summary>
    public CurrencySymbolPlacement SymbolPlacement { get; set; } = CurrencySymbolPlacement.After;
}

// =============================================================================
// Class: CrmFieldMeta
// Holds field metadata needed for search index value formatting
// =============================================================================

/// <summary>
/// Contains metadata for a single CRM entity field, used by
/// <see cref="SearchService.GetStringValue"/> to apply field-type-specific
/// formatting when building the x_search index string.
/// </summary>
public class CrmFieldMeta
{
    /// <summary>The field name as stored in DynamoDB (snake_case, e.g., "first_name").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The field type enum controlling formatting behavior.</summary>
    public CrmFieldType FieldType { get; set; }

    /// <summary>
    /// Display format string for AutoNumber fields.
    /// Source: AutoNumberField.DisplayFormat (SearchService.cs line 174).
    /// Example: "{0}" formats the auto-number value.
    /// </summary>
    public string? DisplayFormat { get; set; }

    /// <summary>
    /// Decimal places for Number and Percent fields.
    /// Source: NumberField.DecimalPlaces (line 251), PercentField.DecimalPlaces (line 259).
    /// </summary>
    public byte? DecimalPlaces { get; set; }

    /// <summary>
    /// Currency metadata for Currency fields.
    /// Source: CurrencyField + CurrencyType (lines 183-193).
    /// </summary>
    public CurrencyInfo? Currency { get; set; }

    /// <summary>
    /// Date/DateTime format string.
    /// Source: DateField.Format (line 200), DateTimeField.Format (line 208).
    /// </summary>
    public string? DateFormat { get; set; }

    /// <summary>
    /// Available options for Select and MultiSelect fields.
    /// Source: SelectField.Options / MultiSelectField.Options (lines 215, 267).
    /// </summary>
    public List<SelectOption>? Options { get; set; }
}

// =============================================================================
// Static class: SearchIndexConfiguration
// EXACT copy from WebVella.Erp.Plugins.Next/Configuration.cs (lines 9-19)
// Only account and contact fields — case/task belong to Inventory/Project service
// =============================================================================

/// <summary>
/// Defines the indexed field lists for CRM entity search index generation.
/// These lists are exact copies from the monolith's Configuration.cs, specifying
/// which fields (including relation-resolved fields) are concatenated into the
/// x_search composite search string.
/// </summary>
public static class SearchIndexConfiguration
{
    /// <summary>
    /// Account entity search index fields — 17 entries.
    /// Exact copy from source Configuration.cs line 9.
    /// Includes <c>$country_1n_account.label</c> for country name resolution.
    /// </summary>
    public static IReadOnlyList<string> AccountSearchIndexFields { get; } = new List<string>
    {
        "city",
        "$country_1n_account.label",
        "email",
        "fax_phone",
        "first_name",
        "fixed_phone",
        "last_name",
        "mobile_phone",
        "name",
        "notes",
        "post_code",
        "region",
        "street",
        "street_2",
        "tax_id",
        "type",
        "website"
    }.AsReadOnly();

    /// <summary>
    /// Contact entity search index fields — 15 entries.
    /// Exact copy from source Configuration.cs line 13.
    /// Includes <c>$country_1n_contact.label</c> and <c>$account_nn_contact.name</c>.
    /// </summary>
    public static IReadOnlyList<string> ContactSearchIndexFields { get; } = new List<string>
    {
        "city",
        "$country_1n_contact.label",
        "$account_nn_contact.name",
        "email",
        "fax_phone",
        "first_name",
        "fixed_phone",
        "job_title",
        "last_name",
        "mobile_phone",
        "notes",
        "post_code",
        "region",
        "street",
        "street_2"
    }.AsReadOnly();
}

// =============================================================================
// Internal: RelationDefinition — Describes a CRM entity relation for search
// =============================================================================

/// <summary>
/// Describes a relation between two CRM entities, used for resolving
/// <c>$relation_name.field_name</c> tokens in search index field lists.
/// Replaces the runtime EntityRelationManager().Read() call from the monolith.
/// </summary>
internal sealed class RelationDefinition
{
    /// <summary>Relation name (e.g., "country_1n_account").</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The origin (source) entity name in the relation.</summary>
    public string OriginEntityName { get; init; } = string.Empty;

    /// <summary>The target entity name in the relation.</summary>
    public string TargetEntityName { get; init; } = string.Empty;

    /// <summary>
    /// The field name on the origin entity that is selected during search indexing.
    /// For country_1n_account, this is "label" (from the country entity).
    /// </summary>
    public string OriginFieldName { get; init; } = string.Empty;

    /// <summary>
    /// The foreign key field name on the target entity that references the origin.
    /// For 1:N relations, the target record holds the FK (e.g., account.country_id).
    /// Null for N:N relations which use a separate join mechanism.
    /// </summary>
    public string? ForeignKeyFieldName { get; init; }

    /// <summary>Whether this is a many-to-many relation (true) or one-to-many (false).</summary>
    public bool IsManyToMany { get; init; }
}

// =============================================================================
// Internal static: CrmEntitySchema — Locally owned entity/relation metadata
// Replaces runtime calls to EntityManager().ReadEntities() and
// EntityRelationManager().Read() from source SearchService.cs lines 18-19
// =============================================================================

/// <summary>
/// Contains all CRM bounded-context entity and relation metadata needed for
/// search index generation. This metadata is locally owned because the CRM
/// service owns account, contact, address, salutation, and country entities.
/// No runtime metadata fetching from external services.
/// </summary>
internal static class CrmEntitySchema
{
    // =========================================================================
    // Entity identity — maps entity names to their canonical GUIDs
    // Uses Account.EntityId and Contact.EntityId from domain models
    // =========================================================================

    /// <summary>
    /// Maps entity names to their canonical entity IDs. Uses the static EntityId
    /// fields from <see cref="Account"/> and <see cref="Contact"/> domain models.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Guid> EntityIds =
        new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = Account.EntityId,
            ["contact"] = Contact.EntityId
        };

    // =========================================================================
    // Entity field metadata — keyed by entity name, then field name
    // Replaces: var currentEntity = entities.FirstOrDefault(x => x.Name == entityName)
    //           currentEntity.Fields.FirstOrDefault(x => x.Name == fieldName)
    // =========================================================================

    /// <summary>
    /// Maps entity names to their field metadata dictionaries.
    /// Each inner dictionary maps field names (snake_case) to CrmFieldMeta.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, CrmFieldMeta>> EntityFieldMetadata =
        new Dictionary<string, IReadOnlyDictionary<string, CrmFieldMeta>>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = BuildAccountFieldMetadata(),
            ["contact"] = BuildContactFieldMetadata(),
            ["country"] = BuildCountryFieldMetadata()
        };

    // =========================================================================
    // Relation definitions — keyed by relation name
    // Replaces: var relation = relations.FirstOrDefault(x => x.Name == relationName)
    // =========================================================================

    /// <summary>
    /// Maps relation names to their definitions. Only relations used by the
    /// search index are included.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, RelationDefinition> Relations =
        new Dictionary<string, RelationDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["country_1n_account"] = new RelationDefinition
            {
                Name = "country_1n_account",
                OriginEntityName = "country",
                TargetEntityName = "account",
                OriginFieldName = "label",
                ForeignKeyFieldName = "country_id",
                IsManyToMany = false
            },
            ["country_1n_contact"] = new RelationDefinition
            {
                Name = "country_1n_contact",
                OriginEntityName = "country",
                TargetEntityName = "contact",
                OriginFieldName = "label",
                ForeignKeyFieldName = "country_id",
                IsManyToMany = false
            },
            ["account_nn_contact"] = new RelationDefinition
            {
                Name = "account_nn_contact",
                OriginEntityName = "account",
                TargetEntityName = "contact",
                OriginFieldName = "name",
                ForeignKeyFieldName = null,
                IsManyToMany = true
            }
        };

    // =========================================================================
    // Account field metadata builder
    // =========================================================================

    private static IReadOnlyDictionary<string, CrmFieldMeta> BuildAccountFieldMetadata()
    {
        var fields = new Dictionary<string, CrmFieldMeta>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new CrmFieldMeta { Name = "id", FieldType = CrmFieldType.Guid },
            ["name"] = new CrmFieldMeta { Name = "name", FieldType = CrmFieldType.Text },
            ["type"] = new CrmFieldMeta
            {
                Name = "type",
                FieldType = CrmFieldType.Select,
                Options = new List<SelectOption>
                {
                    new SelectOption { Value = "1", Label = "Company" },
                    new SelectOption { Value = "2", Label = "Person" }
                }
            },
            ["email"] = new CrmFieldMeta { Name = "email", FieldType = CrmFieldType.Email },
            ["website"] = new CrmFieldMeta { Name = "website", FieldType = CrmFieldType.Url },
            ["city"] = new CrmFieldMeta { Name = "city", FieldType = CrmFieldType.Text },
            ["street"] = new CrmFieldMeta { Name = "street", FieldType = CrmFieldType.Text },
            ["street_2"] = new CrmFieldMeta { Name = "street_2", FieldType = CrmFieldType.Text },
            ["region"] = new CrmFieldMeta { Name = "region", FieldType = CrmFieldType.Text },
            ["post_code"] = new CrmFieldMeta { Name = "post_code", FieldType = CrmFieldType.Text },
            ["first_name"] = new CrmFieldMeta { Name = "first_name", FieldType = CrmFieldType.Text },
            ["last_name"] = new CrmFieldMeta { Name = "last_name", FieldType = CrmFieldType.Text },
            ["fixed_phone"] = new CrmFieldMeta { Name = "fixed_phone", FieldType = CrmFieldType.Phone },
            ["mobile_phone"] = new CrmFieldMeta { Name = "mobile_phone", FieldType = CrmFieldType.Phone },
            ["fax_phone"] = new CrmFieldMeta { Name = "fax_phone", FieldType = CrmFieldType.Phone },
            ["notes"] = new CrmFieldMeta { Name = "notes", FieldType = CrmFieldType.MultiLineText },
            ["tax_id"] = new CrmFieldMeta { Name = "tax_id", FieldType = CrmFieldType.Text },
            ["x_search"] = new CrmFieldMeta { Name = "x_search", FieldType = CrmFieldType.Text },
            ["country_id"] = new CrmFieldMeta { Name = "country_id", FieldType = CrmFieldType.Guid },
            ["language_id"] = new CrmFieldMeta { Name = "language_id", FieldType = CrmFieldType.Guid },
            ["currency_id"] = new CrmFieldMeta { Name = "currency_id", FieldType = CrmFieldType.Guid },
            ["salutation_id"] = new CrmFieldMeta { Name = "salutation_id", FieldType = CrmFieldType.Guid },
            ["created_on"] = new CrmFieldMeta
            {
                Name = "created_on",
                FieldType = CrmFieldType.DateTime,
                DateFormat = "yyyy-MM-dd HH:mm:ss"
            },
            ["photo"] = new CrmFieldMeta { Name = "photo", FieldType = CrmFieldType.Image },
            ["l_scope"] = new CrmFieldMeta { Name = "l_scope", FieldType = CrmFieldType.Text }
        };
        return fields;
    }

    // =========================================================================
    // Contact field metadata builder
    // =========================================================================

    private static IReadOnlyDictionary<string, CrmFieldMeta> BuildContactFieldMetadata()
    {
        var fields = new Dictionary<string, CrmFieldMeta>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new CrmFieldMeta { Name = "id", FieldType = CrmFieldType.Guid },
            ["email"] = new CrmFieldMeta { Name = "email", FieldType = CrmFieldType.Email },
            ["job_title"] = new CrmFieldMeta { Name = "job_title", FieldType = CrmFieldType.Text },
            ["first_name"] = new CrmFieldMeta { Name = "first_name", FieldType = CrmFieldType.Text },
            ["last_name"] = new CrmFieldMeta { Name = "last_name", FieldType = CrmFieldType.Text },
            ["notes"] = new CrmFieldMeta { Name = "notes", FieldType = CrmFieldType.MultiLineText },
            ["fixed_phone"] = new CrmFieldMeta { Name = "fixed_phone", FieldType = CrmFieldType.Phone },
            ["mobile_phone"] = new CrmFieldMeta { Name = "mobile_phone", FieldType = CrmFieldType.Phone },
            ["fax_phone"] = new CrmFieldMeta { Name = "fax_phone", FieldType = CrmFieldType.Phone },
            ["salutation_id"] = new CrmFieldMeta { Name = "salutation_id", FieldType = CrmFieldType.Guid },
            ["city"] = new CrmFieldMeta { Name = "city", FieldType = CrmFieldType.Text },
            ["country_id"] = new CrmFieldMeta { Name = "country_id", FieldType = CrmFieldType.Guid },
            ["region"] = new CrmFieldMeta { Name = "region", FieldType = CrmFieldType.Text },
            ["street"] = new CrmFieldMeta { Name = "street", FieldType = CrmFieldType.Text },
            ["street_2"] = new CrmFieldMeta { Name = "street_2", FieldType = CrmFieldType.Text },
            ["post_code"] = new CrmFieldMeta { Name = "post_code", FieldType = CrmFieldType.Text },
            ["created_on"] = new CrmFieldMeta
            {
                Name = "created_on",
                FieldType = CrmFieldType.DateTime,
                DateFormat = "yyyy-MM-dd HH:mm:ss"
            },
            ["photo"] = new CrmFieldMeta { Name = "photo", FieldType = CrmFieldType.Image },
            ["x_search"] = new CrmFieldMeta { Name = "x_search", FieldType = CrmFieldType.Text },
            ["l_scope"] = new CrmFieldMeta { Name = "l_scope", FieldType = CrmFieldType.Text }
        };
        return fields;
    }

    // =========================================================================
    // Country field metadata builder (referenced entity for 1:N relations)
    // =========================================================================

    private static IReadOnlyDictionary<string, CrmFieldMeta> BuildCountryFieldMetadata()
    {
        var fields = new Dictionary<string, CrmFieldMeta>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new CrmFieldMeta { Name = "id", FieldType = CrmFieldType.Guid },
            ["label"] = new CrmFieldMeta { Name = "label", FieldType = CrmFieldType.Text },
            ["name"] = new CrmFieldMeta { Name = "name", FieldType = CrmFieldType.Text }
        };
        return fields;
    }
}

// =============================================================================
// ISearchService — Public interface for DI registration and testability
// =============================================================================

/// <summary>
/// Generates and persists the denormalized <c>x_search</c> full-text search
/// index string on CRM entity records (account, contact).
/// </summary>
public interface ISearchService
{
    /// <summary>
    /// Regenerates the <c>x_search</c> field on a specific CRM record by
    /// formatting the values of the given indexed fields into a single
    /// space-separated search string, then persisting it via
    /// <see cref="ICrmRepository.UpdateSearchFieldAsync"/>.
    /// </summary>
    /// <param name="entityName">CRM entity name (e.g., "account", "contact").</param>
    /// <param name="recordId">The unique record ID.</param>
    /// <param name="indexedFields">
    /// List of field names to include in the search index. May contain
    /// relation references in the format <c>$relation_name.field_name</c>.
    /// </param>
    /// <param name="ct">Cancellation token for Lambda graceful shutdown.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="entityName"/> is not a recognized CRM entity.
    /// </exception>
    Task RegenSearchFieldAsync(
        string entityName,
        Guid recordId,
        List<string> indexedFields,
        CancellationToken ct = default);

    /// <summary>
    /// Generates the search index string for a specific CRM record without
    /// persisting it. Useful for testing and validation scenarios.
    /// </summary>
    /// <param name="entityName">CRM entity name (e.g., "account", "contact").</param>
    /// <param name="recordId">The unique record ID.</param>
    /// <param name="indexedFields">
    /// List of field names to include in the search index.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The generated search index string, or empty if record not found.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="entityName"/> is not a recognized CRM entity.
    /// </exception>
    Task<string> GenerateSearchIndexAsync(
        string entityName,
        Guid recordId,
        List<string> indexedFields,
        CancellationToken ct = default);
}

// =============================================================================
// SearchService — Full implementation of CRM search index regeneration
// =============================================================================

/// <summary>
/// Generates and persists a denormalized full-text search index string
/// (<c>x_search</c>) on CRM entity records. This service formats field values
/// according to field-type-specific rules and resolves related entity fields
/// via explicit DynamoDB queries.
///
/// <para>
/// Replaces <c>WebVella.Erp.Plugins.Next/Services/SearchService.cs</c>.
/// Key differences: async, DI-injected, DynamoDB-backed, locally owned metadata,
/// structured logging, CancellationToken support.
/// </para>
/// </summary>
public sealed class SearchService : ISearchService
{
    private readonly ICrmRepository _crmRepository;
    private readonly ILogger<SearchService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchService"/> class.
    /// Replaces the monolith's <c>new SearchService()</c> + <c>BaseService</c>
    /// inheritance with constructor-injected dependencies.
    /// </summary>
    /// <param name="crmRepository">DynamoDB data access for CRM entities.</param>
    /// <param name="logger">Structured logger for correlation-ID logging.</param>
    public SearchService(ICrmRepository crmRepository, ILogger<SearchService> logger)
    {
        _crmRepository = crmRepository ?? throw new ArgumentNullException(nameof(crmRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // =========================================================================
    // RegenSearchFieldAsync — Main entry point
    // Replaces source SearchService.RegenSearchField (lines 16-160)
    // =========================================================================

    /// <inheritdoc />
    public async Task RegenSearchFieldAsync(
        string entityName,
        Guid recordId,
        List<string> indexedFields,
        CancellationToken ct = default)
    {
        var searchIndex = await GenerateSearchIndexAsync(entityName, recordId, indexedFields, ct)
            .ConfigureAwait(false);

        // Persist x_search value via CrmRepository — direct DynamoDB attribute update.
        // CRITICAL: This does NOT trigger domain events, matching the source's
        // RecordManager(executeHooks: false) pattern (source line 151).
        try
        {
            await _crmRepository.UpdateSearchFieldAsync(entityName, recordId, searchIndex, ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Search index regenerated for entity={EntityName}, recordId={RecordId}, fieldCount={FieldCount}, indexLength={IndexLength}",
                entityName, recordId, indexedFields.Count, searchIndex.Length);
        }
        catch (Exception ex)
        {
            // Matches source lines 152-158: throw on update failure
            _logger.LogError(ex,
                "Failed to update x_search for entity={EntityName}, recordId={RecordId}",
                entityName, recordId);
            throw new InvalidOperationException(
                $"Search index update failed for entity {entityName}, record {recordId}: {ex.Message}", ex);
        }
    }

    // =========================================================================
    // GenerateSearchIndexAsync — Generates the search string without persisting
    // =========================================================================

    /// <inheritdoc />
    public async Task<string> GenerateSearchIndexAsync(
        string entityName,
        Guid recordId,
        List<string> indexedFields,
        CancellationToken ct = default)
    {
        // Step 1: Validate entity — replaces source line 20-22
        if (!CrmEntitySchema.EntityFieldMetadata.ContainsKey(entityName))
        {
            throw new ArgumentException($"Search index generation failed: Entity {entityName} not found");
        }

        var entityFields = CrmEntitySchema.EntityFieldMetadata[entityName];

        // Step 2: Resolve request columns — replaces source lines 25-68
        // Early return if no fields to index
        if (!indexedFields.Any())
        {
            return string.Empty;
        }

        var directColumns = indexedFields.Where(f => !f.StartsWith('$')).ToList();
        var relationTokens = indexedFields.Where(f => f.StartsWith('$')).ToList();

        // Validate direct columns against entity field metadata
        var validDirectColumns = new List<string>();
        var relationColumns = new List<(string RelationName, string FieldName, string FullToken)>();

        foreach (var field in directColumns)
        {
            // Direct field — validate it exists on the entity (source lines 31-33)
            if (entityFields.ContainsKey(field))
            {
                validDirectColumns.Add(field);
            }
            else
            {
                _logger.LogWarning(
                    "Skipping unknown direct field '{FieldName}' for entity '{EntityName}'",
                    field, entityName);
            }
        }

        foreach (var field in relationTokens)
        {
            // Relation field — parse $relation_name.field_name (source lines 37-64)
            if (TryParseRelationToken(field, entityName, out var relationName, out var relatedFieldName))
            {
                relationColumns.Add((relationName, relatedFieldName, field));
            }
            else
            {
                _logger.LogWarning(
                    "Skipping invalid relation token '{Token}' for entity '{EntityName}'",
                    field, entityName);
            }
        }

        // Step 3: Fetch record from DynamoDB — replaces source lines 72-74 (EQL query)
        var recordDict = await FetchRecordAsDictionaryAsync(entityName, recordId, ct)
            .ConfigureAwait(false);

        // Step 4: Build search index string — replaces source lines 77-143
        var searchIndex = string.Empty;

        if (recordDict != null)
        {
            // Process direct fields (source lines 82-98)
            foreach (var columnName in validDirectColumns)
            {
                try
                {
                    if (recordDict.TryGetValue(columnName, out var value) && value != null)
                    {
                        var stringValue = GetStringValue(columnName, entityName, recordDict);
                        if (!string.IsNullOrWhiteSpace(stringValue))
                        {
                            searchIndex += stringValue + " ";
                        }
                    }
                }
                catch
                {
                    // Matches source lines 94-97: swallow exceptions during field formatting
                    // "Do nothing" — preserve exact monolith behavior
                }
            }

            // Process relation fields (source lines 100-141)
            foreach (var (relationName, relatedFieldName, fullToken) in relationColumns)
            {
                try
                {
                    var relatedRecords = await GetRelatedRecordsAsync(
                        entityName, recordDict, relationName, ct).ConfigureAwait(false);

                    foreach (var relatedRecord in relatedRecords)
                    {
                        if (relatedRecord.TryGetValue(relatedFieldName, out var relValue) && relValue != null)
                        {
                            var stringVal = relValue.ToString();
                            if (!string.IsNullOrWhiteSpace(stringVal))
                            {
                                searchIndex += stringVal + " ";
                            }
                        }
                    }
                }
                catch
                {
                    // Matches source lines 135-138: swallow exceptions during relation resolution
                    // "Do nothing" — preserve exact monolith behavior
                }
            }
        }
        // else: record not found — matches source lines 144-146: "Do nothing"

        return searchIndex;
    }

    // =========================================================================
    // TryParseRelationToken — Validates $relation_name.field_name format
    // Replaces source lines 37-64
    // =========================================================================

    /// <summary>
    /// Parses a relation token like <c>$country_1n_account.label</c> into its
    /// relation name and field name components, validating that the relation
    /// exists and that the current entity participates in it.
    /// </summary>
    private bool TryParseRelationToken(
        string token,
        string entityName,
        out string relationName,
        out string fieldName)
    {
        relationName = string.Empty;
        fieldName = string.Empty;

        // Remove the leading "$" and split on "."
        var stripped = token.TrimStart('$');
        var parts = stripped.Split('.', StringSplitOptions.RemoveEmptyEntries);

        // Must have exactly 2 parts: relation_name.field_name (source lines 40-41)
        if (parts.Length != 2)
        {
            return false;
        }

        relationName = parts[0];
        fieldName = parts[1];

        // Look up relation in CrmEntitySchema — replaces source line 43
        if (!CrmEntitySchema.Relations.TryGetValue(relationName, out var relation))
        {
            return false;
        }

        // Verify current entity participates in the relation (source lines 48-51)
        var isOrigin = string.Equals(relation.OriginEntityName, entityName, StringComparison.OrdinalIgnoreCase);
        var isTarget = string.Equals(relation.TargetEntityName, entityName, StringComparison.OrdinalIgnoreCase);

        if (!isOrigin && !isTarget)
        {
            return false;
        }

        // Determine the "other side" entity and verify the field exists there
        var otherEntityName = isTarget ? relation.OriginEntityName : relation.TargetEntityName;

        if (!CrmEntitySchema.EntityFieldMetadata.TryGetValue(otherEntityName, out var otherFields))
        {
            return false;
        }

        if (!otherFields.ContainsKey(fieldName))
        {
            return false;
        }

        return true;
    }

    // =========================================================================
    // FetchRecordAsDictionaryAsync — Fetches a CRM record as a field dictionary
    // Replaces source lines 72-74 (EQL query)
    // =========================================================================

    /// <summary>
    /// Fetches a record from DynamoDB and converts it to a dictionary of
    /// field name → value pairs. Uses explicit property mapping for AOT
    /// compatibility (no reflection-based serialization).
    /// </summary>
    private async Task<Dictionary<string, object?>?> FetchRecordAsDictionaryAsync(
        string entityName,
        Guid recordId,
        CancellationToken ct)
    {
        if (string.Equals(entityName, "account", StringComparison.OrdinalIgnoreCase))
        {
            var account = await _crmRepository.GetByIdAsync<Account>(entityName, recordId, ct)
                .ConfigureAwait(false);
            return account != null ? AccountToDictionary(account) : null;
        }
        else if (string.Equals(entityName, "contact", StringComparison.OrdinalIgnoreCase))
        {
            var contact = await _crmRepository.GetByIdAsync<Contact>(entityName, recordId, ct)
                .ConfigureAwait(false);
            return contact != null ? ContactToDictionary(contact) : null;
        }

        return null;
    }

    // =========================================================================
    // GetRelatedRecordsAsync — Resolves related entity records for relation fields
    // Replaces the EQL inline relation resolution from the monolith
    // =========================================================================

    /// <summary>
    /// Fetches related records for a given relation, returning them as
    /// dictionaries of field name → value pairs.
    /// For 1:N relations: looks up the FK on the current record, fetches the
    /// related origin record by ID.
    /// For N:N relations: queries all related records by the relationship.
    /// </summary>
    private async Task<List<Dictionary<string, object?>>> GetRelatedRecordsAsync(
        string entityName,
        Dictionary<string, object?> currentRecord,
        string relationName,
        CancellationToken ct)
    {
        var results = new List<Dictionary<string, object?>>();

        if (!CrmEntitySchema.Relations.TryGetValue(relationName, out var relation))
        {
            return results;
        }

        if (!relation.IsManyToMany)
        {
            // 1:N relation (e.g., country_1n_account, country_1n_contact)
            // The current entity is the target — it holds the FK to the origin.
            // FK field name is stored in the relation definition (e.g., "country_id").
            if (relation.ForeignKeyFieldName == null)
            {
                return results;
            }

            if (!currentRecord.TryGetValue(relation.ForeignKeyFieldName, out var fkValue) || fkValue == null)
            {
                return results;
            }

            // Parse the FK value as a Guid
            Guid relatedId;
            if (fkValue is Guid guidFk)
            {
                relatedId = guidFk;
            }
            else if (Guid.TryParse(fkValue.ToString(), out var parsedGuid))
            {
                relatedId = parsedGuid;
            }
            else
            {
                return results;
            }

            // Fetch the related origin record (e.g., fetch country by country_id)
            var relatedDict = await FetchGenericRecordAsDictionaryAsync(
                relation.OriginEntityName, relatedId, ct).ConfigureAwait(false);

            if (relatedDict != null)
            {
                results.Add(relatedDict);
            }
        }
        else
        {
            // N:N relation (e.g., account_nn_contact)
            // Determine which entity we need to query from the "other side"
            var isOrigin = string.Equals(relation.OriginEntityName, entityName, StringComparison.OrdinalIgnoreCase);
            var otherEntityName = isOrigin ? relation.TargetEntityName : relation.OriginEntityName;

            // For N:N, we need the current record's ID to query related records
            if (!currentRecord.TryGetValue("id", out var idValue) || idValue == null)
            {
                return results;
            }

            Guid currentId;
            if (idValue is Guid guidId)
            {
                currentId = guidId;
            }
            else if (Guid.TryParse(idValue.ToString(), out var parsedId))
            {
                currentId = parsedId;
            }
            else
            {
                return results;
            }

            // Query for related records. For account_nn_contact when processing a
            // contact record: query accounts that are related to this contact.
            // The CrmRepository QueryAsync is used with a filter on the relation.
            // The relation join data in DynamoDB may be stored as a list attribute
            // on one side or as separate relation items. We query the other entity
            // and filter by the current record's participation in the relation.
            try
            {
                var filter = new QueryFilter
                {
                    FieldName = $"$$relation_{relationName}",
                    Operator = FilterOperator.Contains,
                    Value = currentId.ToString()
                };

                if (string.Equals(otherEntityName, "account", StringComparison.OrdinalIgnoreCase))
                {
                    var relatedAccounts = await _crmRepository.QueryAsync<Account>(
                        otherEntityName, filter, null, null, ct).ConfigureAwait(false);

                    foreach (var acct in relatedAccounts)
                    {
                        results.Add(AccountToDictionary(acct));
                    }
                }
                else if (string.Equals(otherEntityName, "contact", StringComparison.OrdinalIgnoreCase))
                {
                    var relatedContacts = await _crmRepository.QueryAsync<Contact>(
                        otherEntityName, filter, null, null, ct).ConfigureAwait(false);

                    foreach (var ctc in relatedContacts)
                    {
                        results.Add(ContactToDictionary(ctc));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "N:N relation query failed for relation={RelationName}, entity={EntityName}",
                    relationName, entityName);
                // Return empty results — graceful degradation matching monolith behavior
            }
        }

        return results;
    }

    // =========================================================================
    // FetchGenericRecordAsDictionaryAsync — Fetches any known entity by ID
    // Used for related entities (e.g., country) that are not Account/Contact
    // =========================================================================

    /// <summary>
    /// Fetches a record by entity name and ID, returning it as a simple
    /// dictionary. For entities that are not Account/Contact (e.g., country),
    /// uses a generic dictionary approach via the repository.
    /// </summary>
    private async Task<Dictionary<string, object?>?> FetchGenericRecordAsDictionaryAsync(
        string entityName,
        Guid recordId,
        CancellationToken ct)
    {
        if (string.Equals(entityName, "account", StringComparison.OrdinalIgnoreCase))
        {
            var account = await _crmRepository.GetByIdAsync<Account>(entityName, recordId, ct)
                .ConfigureAwait(false);
            return account != null ? AccountToDictionary(account) : null;
        }
        else if (string.Equals(entityName, "contact", StringComparison.OrdinalIgnoreCase))
        {
            var contact = await _crmRepository.GetByIdAsync<Contact>(entityName, recordId, ct)
                .ConfigureAwait(false);
            return contact != null ? ContactToDictionary(contact) : null;
        }
        else
        {
            // For other entities (country, salutation, address), use Dictionary<string, object?>
            // directly. The repository's generic GetByIdAsync with DynamicallyAccessedMembers
            // constraint can handle Dictionary<string, object?> for simple key-value access.
            var record = await _crmRepository.GetByIdAsync<Dictionary<string, object?>>(
                entityName, recordId, ct).ConfigureAwait(false);
            return record;
        }
    }

    // =========================================================================
    // GetStringValue — Field type-specific value formatting
    // Replaces source SearchService.GetStringValue (lines 162-286)
    // =========================================================================

    /// <summary>
    /// Formats a field value from a CRM record to a search-friendly string
    /// based on the field's type metadata. Returns empty string if the field
    /// value is null or missing.
    /// </summary>
    /// <param name="fieldName">The field name (snake_case).</param>
    /// <param name="entityName">The entity name for field metadata lookup.</param>
    /// <param name="record">The record dictionary containing field values.</param>
    /// <returns>Formatted string value, or empty string if not applicable.</returns>
    private static string GetStringValue(
        string fieldName,
        string entityName,
        Dictionary<string, object?> record)
    {
        // Source lines 164-165: return empty if field missing or null
        if (!record.TryGetValue(fieldName, out var rawValue) || rawValue == null)
        {
            return string.Empty;
        }

        // Look up field metadata from CRM entity schema
        if (!CrmEntitySchema.EntityFieldMetadata.TryGetValue(entityName, out var entityFields)
            || !entityFields.TryGetValue(fieldName, out var fieldMeta))
        {
            // Field not in metadata — use default ToString()
            return rawValue.ToString() ?? string.Empty;
        }

        var stringValue = string.Empty;

        switch (fieldMeta.FieldType)
        {
            // =================================================================
            // AutoNumber — source lines 170-177
            // Format: string.Format(DisplayFormat, value.ToString("N0"))
            // =================================================================
            case CrmFieldType.AutoNumber:
            {
                if (!string.IsNullOrWhiteSpace(fieldMeta.DisplayFormat))
                {
                    var numericVal = ConvertToDecimal(rawValue);
                    if (numericVal.HasValue)
                    {
                        stringValue = string.Format(
                            CultureInfo.InvariantCulture,
                            fieldMeta.DisplayFormat,
                            numericVal.Value.ToString("N0", CultureInfo.InvariantCulture));
                    }
                }
                break;
            }

            // =================================================================
            // Currency — source lines 179-193
            // Format: "{Code} {Symbol}{Amount}" or "{Code} {Amount}{Symbol}"
            // =================================================================
            case CrmFieldType.Currency:
            {
                if (fieldMeta.Currency != null)
                {
                    var numericVal = ConvertToDecimal(rawValue);
                    if (numericVal.HasValue)
                    {
                        var currency = fieldMeta.Currency;
                        var formattedAmount = numericVal.Value.ToString(
                            "N" + currency.DecimalDigits, CultureInfo.InvariantCulture);

                        stringValue = currency.SymbolPlacement == CurrencySymbolPlacement.Before
                            ? $"{currency.Code} {currency.SymbolNative}{formattedAmount}"
                            : $"{currency.Code} {formattedAmount}{currency.SymbolNative}";
                    }
                }
                break;
            }

            // =================================================================
            // Date — source lines 196-201
            // Format: DateTime.ToString(DateFormat)
            // =================================================================
            case CrmFieldType.Date:
            {
                var dateVal = ConvertToDateTime(rawValue);
                if (dateVal.HasValue && !string.IsNullOrWhiteSpace(fieldMeta.DateFormat))
                {
                    stringValue = dateVal.Value.ToString(fieldMeta.DateFormat, CultureInfo.InvariantCulture);
                }
                break;
            }

            // =================================================================
            // DateTime — source lines 203-209
            // Format: DateTime.ToString(DateFormat)
            // =================================================================
            case CrmFieldType.DateTime:
            {
                var dateVal = ConvertToDateTime(rawValue);
                if (dateVal.HasValue && !string.IsNullOrWhiteSpace(fieldMeta.DateFormat))
                {
                    stringValue = dateVal.Value.ToString(fieldMeta.DateFormat, CultureInfo.InvariantCulture);
                }
                break;
            }

            // =================================================================
            // MultiSelect — source lines 212-241
            // Values can be List<string> or comma-separated string.
            // Resolve each value to its option label (case-insensitive match).
            // =================================================================
            case CrmFieldType.MultiSelect:
            {
                var selectedValues = new List<string>();

                if (rawValue is List<string> listValues)
                {
                    selectedValues.AddRange(listValues);
                }
                else if (rawValue is IEnumerable<object> enumValues)
                {
                    selectedValues.AddRange(enumValues.Select(v => v?.ToString() ?? string.Empty));
                }
                else
                {
                    // Comma-separated string (source lines 218-229)
                    var rawStr = rawValue.ToString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(rawStr))
                    {
                        selectedValues.AddRange(rawStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(v => v.Trim()));
                    }
                }

                foreach (var val in selectedValues)
                {
                    if (string.IsNullOrWhiteSpace(val))
                    {
                        continue;
                    }

                    // Source line 232: case-insensitive value match for option label
                    var matchingOption = fieldMeta.Options?.FirstOrDefault(
                        x => string.Equals(x.Value, val, StringComparison.OrdinalIgnoreCase));

                    stringValue += (matchingOption != null ? matchingOption.Label : val) + " ";
                }

                stringValue = stringValue.TrimEnd();
                break;
            }

            // =================================================================
            // Password — source lines 244-245: intentionally ignored
            // =================================================================
            case CrmFieldType.Password:
            {
                // EXACT match: "//ignore" — passwords are never indexed
                break;
            }

            // =================================================================
            // Number — source lines 248-252
            // Format: decimal.ToString("N" + DecimalPlaces)
            // =================================================================
            case CrmFieldType.Number:
            {
                var numericVal = ConvertToDecimal(rawValue);
                if (numericVal.HasValue)
                {
                    var decimalPlaces = fieldMeta.DecimalPlaces ?? 2;
                    stringValue = numericVal.Value.ToString(
                        "N" + decimalPlaces, CultureInfo.InvariantCulture);
                }
                break;
            }

            // =================================================================
            // Percent — source lines 255-260
            // Format: decimal.ToString("P" + DecimalPlaces)
            // =================================================================
            case CrmFieldType.Percent:
            {
                var numericVal = ConvertToDecimal(rawValue);
                if (numericVal.HasValue)
                {
                    var decimalPlaces = fieldMeta.DecimalPlaces ?? 2;
                    stringValue = numericVal.Value.ToString(
                        "P" + decimalPlaces, CultureInfo.InvariantCulture);
                }
                break;
            }

            // =================================================================
            // Select — source lines 263-278
            // Match value to option label (case-insensitive), fallback to raw value.
            // =================================================================
            case CrmFieldType.Select:
            {
                var rawStr = rawValue.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(rawStr))
                {
                    // Source line 268: case-insensitive value match
                    var matchingOption = fieldMeta.Options?.FirstOrDefault(
                        x => string.Equals(x.Value, rawStr, StringComparison.OrdinalIgnoreCase));

                    stringValue = matchingOption != null ? matchingOption.Label : rawStr;
                }
                break;
            }

            // =================================================================
            // Default — source lines 280-282
            // Applies to: Text, Email, Phone, Url, MultiLineText, Image, Guid, Boolean
            // =================================================================
            default:
            {
                stringValue = rawValue.ToString() ?? string.Empty;
                break;
            }
        }

        return stringValue;
    }

    // =========================================================================
    // Type conversion helpers — safe conversions for DynamoDB attribute values
    // DynamoDB may return numbers as strings or other representations
    // =========================================================================

    /// <summary>
    /// Safely converts a DynamoDB attribute value to decimal.
    /// Handles numeric types, strings, and null gracefully.
    /// </summary>
    private static decimal? ConvertToDecimal(object? value)
    {
        if (value == null) return null;

        return value switch
        {
            decimal d => d,
            double dbl => (decimal)dbl,
            float f => (decimal)f,
            int i => i,
            long l => l,
            string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>
    /// Safely converts a DynamoDB attribute value to DateTime.
    /// Handles DateTime objects, ISO 8601 strings, and null gracefully.
    /// </summary>
    private static DateTime? ConvertToDateTime(object? value)
    {
        if (value == null) return null;

        return value switch
        {
            DateTime dt => dt,
            DateTimeOffset dto => dto.DateTime,
            string s when DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed) => parsed,
            _ => null
        };
    }

    // =========================================================================
    // Model-to-Dictionary converters — explicit property mapping (AOT-safe)
    // Replaces reflection-based dictionary conversion for Native AOT compat
    // =========================================================================

    /// <summary>
    /// Converts an <see cref="Account"/> model to a dictionary of field name → value pairs.
    /// Uses explicit property mapping for AOT compatibility (no reflection).
    /// </summary>
    private static Dictionary<string, object?> AccountToDictionary(Account account)
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = account.Id,
            ["name"] = account.Name,
            ["type"] = account.Type,
            ["email"] = account.Email,
            ["website"] = account.Website,
            ["city"] = account.City,
            ["street"] = account.Street,
            ["street_2"] = account.Street2,
            ["region"] = account.Region,
            ["post_code"] = account.PostCode,
            ["first_name"] = account.FirstName,
            ["last_name"] = account.LastName,
            ["fixed_phone"] = account.FixedPhone,
            ["mobile_phone"] = account.MobilePhone,
            ["fax_phone"] = account.FaxPhone,
            ["notes"] = account.Notes,
            ["tax_id"] = account.TaxId,
            ["x_search"] = account.XSearch,
            ["country_id"] = account.CountryId,
            ["language_id"] = account.LanguageId,
            ["currency_id"] = account.CurrencyId,
            ["salutation_id"] = account.SalutationId,
            ["created_on"] = account.CreatedOn,
            ["photo"] = account.Photo,
            ["l_scope"] = account.LScope
        };
    }

    /// <summary>
    /// Converts a <see cref="Contact"/> model to a dictionary of field name → value pairs.
    /// Uses explicit property mapping for AOT compatibility (no reflection).
    /// </summary>
    private static Dictionary<string, object?> ContactToDictionary(Contact contact)
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = contact.Id,
            ["email"] = contact.Email,
            ["job_title"] = contact.JobTitle,
            ["first_name"] = contact.FirstName,
            ["last_name"] = contact.LastName,
            ["notes"] = contact.Notes,
            ["fixed_phone"] = contact.FixedPhone,
            ["mobile_phone"] = contact.MobilePhone,
            ["fax_phone"] = contact.FaxPhone,
            ["salutation_id"] = contact.SalutationId,
            ["city"] = contact.City,
            ["country_id"] = contact.CountryId,
            ["region"] = contact.Region,
            ["street"] = contact.Street,
            ["street_2"] = contact.Street2,
            ["post_code"] = contact.PostCode,
            ["created_on"] = contact.CreatedOn,
            ["photo"] = contact.Photo,
            ["x_search"] = contact.XSearch,
            ["l_scope"] = contact.LScope
        };
    }
}
