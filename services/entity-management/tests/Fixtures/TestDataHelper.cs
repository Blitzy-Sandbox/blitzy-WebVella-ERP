// =============================================================================
// TestDataHelper.cs — Shared Test Data Builders for Entity Management Service
// =============================================================================
// Provides reusable builder/factory methods for creating test entity definitions,
// field definitions, relation definitions, record data, CSV test data, and
// DynamoDB item dictionaries used by both Unit and Integration test suites.
//
// Namespace: WebVellaErp.EntityManagement.Tests.Fixtures
//
// Design Principles:
//   - Static class with deterministic test GUIDs for reproducible tests
//   - All 21 field types covered with fluent-style builder methods
//   - DynamoDB single-table design items for direct table seeding in integration tests
//   - System.Text.Json serialization (AOT-compatible, NOT Newtonsoft)
//   - Zero external dependencies beyond AWSSDK.DynamoDBv2 and System.Text.Json
//
// Source Reference: Patterns extracted from WebVella.Erp/Api/Definitions.cs,
//   EntityManager.cs, RecordManager.cs, DbEntityRepository.cs, and all
//   FieldType model files.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using WebVellaErp.EntityManagement.Models;

namespace WebVellaErp.EntityManagement.Tests.Fixtures
{
    /// <summary>
    /// Shared test data builder providing factory methods for all Entity Management
    /// domain objects. Used across unit and integration test suites to create
    /// consistent, well-formed test entities, fields, relations, records, CSV data,
    /// and DynamoDB items.
    /// </summary>
    public static class TestDataHelper
    {
        // =====================================================================
        // Phase 1: Static Constants — Well-Known Test GUIDs
        // =====================================================================

        /// <summary>
        /// Deterministic test entity identifier for reproducible test assertions.
        /// </summary>
        public static readonly Guid TestEntityId = new Guid("11111111-1111-1111-1111-111111111111");

        /// <summary>
        /// Deterministic test field identifier for reproducible test assertions.
        /// </summary>
        public static readonly Guid TestFieldId = new Guid("22222222-2222-2222-2222-222222222222");

        /// <summary>
        /// Deterministic test relation identifier for reproducible test assertions.
        /// </summary>
        public static readonly Guid TestRelationId = new Guid("33333333-3333-3333-3333-333333333333");

        /// <summary>
        /// Deterministic test record identifier for reproducible test assertions.
        /// </summary>
        public static readonly Guid TestRecordId = new Guid("44444444-4444-4444-4444-444444444444");

        // =====================================================================
        // Re-exported System GUIDs from Models/Definitions.cs for easy test access
        // =====================================================================

        /// <summary>System user GUID (automated operations). Mirrors SystemIds.SystemUserId.</summary>
        public static readonly Guid SystemUserId = SystemIds.SystemUserId;

        /// <summary>First human user GUID (bootstrap admin). Mirrors SystemIds.FirstUserId.</summary>
        public static readonly Guid FirstUserId = SystemIds.FirstUserId;

        /// <summary>Administrator role GUID. Mirrors SystemIds.AdministratorRoleId.</summary>
        public static readonly Guid AdministratorRoleId = SystemIds.AdministratorRoleId;

        /// <summary>Regular role GUID. Mirrors SystemIds.RegularRoleId.</summary>
        public static readonly Guid RegularRoleId = SystemIds.RegularRoleId;

        /// <summary>Guest role GUID. Mirrors SystemIds.GuestRoleId.</summary>
        public static readonly Guid GuestRoleId = SystemIds.GuestRoleId;

        /// <summary>User entity GUID. Mirrors SystemIds.UserEntityId.</summary>
        public static readonly Guid UserEntityId = SystemIds.UserEntityId;

        /// <summary>Role entity GUID. Mirrors SystemIds.RoleEntityId.</summary>
        public static readonly Guid RoleEntityId = SystemIds.RoleEntityId;

        /// <summary>System entity GUID. Mirrors SystemIds.SystemEntityId.</summary>
        public static readonly Guid SystemEntityId = SystemIds.SystemEntityId;

        /// <summary>User-to-Role relation GUID. Mirrors SystemIds.UserRoleRelationId.</summary>
        public static readonly Guid UserRoleRelationId = SystemIds.UserRoleRelationId;

        // =====================================================================
        // DynamoDB Table Name Constants (for integration tests)
        // =====================================================================

        /// <summary>DynamoDB table name for entity/field/relation metadata in tests.</summary>
        public const string EntityMetadataTableName = "entity-management-metadata-test";

        /// <summary>DynamoDB table name for record storage in tests.</summary>
        public const string RecordStorageTableName = "entity-management-records-test";

        // =====================================================================
        // SNS Topic ARN Constants (for LocalStack integration tests)
        // =====================================================================

        /// <summary>SNS topic ARN for entity created events.</summary>
        public const string EntityCreatedTopicArn = "arn:aws:sns:us-east-1:000000000000:entity-management-entity-created";

        /// <summary>SNS topic ARN for record created events.</summary>
        public const string RecordCreatedTopicArn = "arn:aws:sns:us-east-1:000000000000:entity-management-record-created";

        /// <summary>SNS topic ARN for record updated events.</summary>
        public const string RecordUpdatedTopicArn = "arn:aws:sns:us-east-1:000000000000:entity-management-record-updated";

        /// <summary>SNS topic ARN for record deleted events.</summary>
        public const string RecordDeletedTopicArn = "arn:aws:sns:us-east-1:000000000000:entity-management-record-deleted";

        // =====================================================================
        // Phase 2: Entity Builder Methods
        // =====================================================================

        /// <summary>
        /// Creates a minimal test entity with a standard "id" GuidField.
        /// Mirrors the minimal entity structure from DbEntityRepository.Create()
        /// which always creates an entity with at least an "id" field.
        /// </summary>
        /// <param name="name">Entity programmatic name (default: "test_entity").</param>
        /// <param name="id">Optional entity ID; generates new if null.</param>
        /// <param name="system">Whether this is a system entity (default: false).</param>
        /// <returns>A fully constructed Entity with id field and default permissions.</returns>
        public static Entity CreateTestEntity(string name = "test_entity", Guid? id = null, bool system = false)
        {
            var entityId = id ?? Guid.NewGuid();
            var label = char.ToUpperInvariant(name[0]) + name.Substring(1).Replace("_", " ") + " Entity";

            var idField = new GuidField
            {
                Id = Guid.NewGuid(),
                Name = "id",
                Label = "Id",
                Required = true,
                Unique = true,
                System = true,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                GenerateNewId = true,
                Permissions = new FieldPermissions()
            };

            return new Entity
            {
                Id = entityId,
                Name = name,
                Label = label,
                LabelPlural = label + "s",
                System = system,
                IconName = "fas fa-database",
                Color = "#2196F3",
                RecordPermissions = new RecordPermissions
                {
                    CanRead = new List<Guid> { AdministratorRoleId, RegularRoleId },
                    CanCreate = new List<Guid> { AdministratorRoleId, RegularRoleId },
                    CanUpdate = new List<Guid> { AdministratorRoleId, RegularRoleId },
                    CanDelete = new List<Guid> { AdministratorRoleId, RegularRoleId }
                },
                Fields = new List<Field> { idField },
                RecordScreenIdField = null
            };
        }

        /// <summary>
        /// Creates a test entity with standard fields: id, created_on, created_by,
        /// last_modified_on, last_modified_by. Mirrors the auto-created fields from
        /// DbEntityRepository which creates user_{entity}_created_by / modified_by relations.
        /// </summary>
        /// <param name="name">Entity programmatic name (default: "test_entity").</param>
        /// <param name="id">Optional entity ID; generates new if null.</param>
        /// <returns>Entity with 5 standard fields.</returns>
        public static Entity CreateTestEntityWithStandardFields(string name = "test_entity", Guid? id = null)
        {
            var entity = CreateTestEntity(name, id);

            var createdOn = new DateTimeField
            {
                Id = Guid.NewGuid(),
                Name = "created_on",
                Label = "Created On",
                Required = true,
                Unique = false,
                System = true,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                UseCurrentTimeAsDefaultValue = true,
                Format = "yyyy-MM-dd HH:mm",
                Permissions = new FieldPermissions()
            };

            var createdBy = new GuidField
            {
                Id = Guid.NewGuid(),
                Name = "created_by",
                Label = "Created By",
                Required = true,
                Unique = false,
                System = true,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                GenerateNewId = false,
                Permissions = new FieldPermissions()
            };

            var lastModifiedOn = new DateTimeField
            {
                Id = Guid.NewGuid(),
                Name = "last_modified_on",
                Label = "Last Modified On",
                Required = false,
                Unique = false,
                System = true,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                UseCurrentTimeAsDefaultValue = true,
                Format = "yyyy-MM-dd HH:mm",
                Permissions = new FieldPermissions()
            };

            var lastModifiedBy = new GuidField
            {
                Id = Guid.NewGuid(),
                Name = "last_modified_by",
                Label = "Last Modified By",
                Required = false,
                Unique = false,
                System = true,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                GenerateNewId = false,
                Permissions = new FieldPermissions()
            };

            entity.Fields.Add(createdOn);
            entity.Fields.Add(createdBy);
            entity.Fields.Add(lastModifiedOn);
            entity.Fields.Add(lastModifiedBy);

            return entity;
        }

        // =====================================================================
        // Phase 3: Field Builder Methods — All 21 Field Types
        // =====================================================================

        /// <summary>
        /// Helper to convert underscore_separated names to Title Case labels.
        /// </summary>
        private static string ToLabel(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var words = name.Split('_');
            return string.Join(" ", words.Select(w =>
                w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w.Substring(1)));
        }

        /// <summary>Creates a GuidField with optional configuration.</summary>
        public static GuidField CreateGuidField(
            string name = "test_guid",
            Guid? id = null,
            bool required = false,
            bool unique = false,
            bool generateNewId = false)
        {
            return new GuidField
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Required = required,
                Unique = unique,
                System = false,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                GenerateNewId = generateNewId,
                Permissions = new FieldPermissions()
            };
        }

        /// <summary>Creates a TextField with optional default value and max length.</summary>
        public static TextField CreateTextField(
            string name = "test_text",
            Guid? id = null,
            string? defaultValue = "",
            int? maxLength = 200)
        {
            return new TextField
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Required = false,
                Unique = false,
                System = false,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                DefaultValue = defaultValue,
                MaxLength = maxLength,
                Permissions = new FieldPermissions()
            };
        }

        /// <summary>Creates a NumberField with optional min/max/decimal configuration.</summary>
        public static NumberField CreateNumberField(
            string name = "test_number",
            Guid? id = null,
            decimal? defaultValue = null,
            decimal? minValue = null,
            decimal? maxValue = null,
            byte? decimalPlaces = 2)
        {
            return new NumberField
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Required = false,
                Unique = false,
                System = false,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                DefaultValue = defaultValue,
                MinValue = minValue,
                MaxValue = maxValue,
                DecimalPlaces = decimalPlaces,
                Permissions = new FieldPermissions()
            };
        }

        /// <summary>Creates a DateField with optional format and current-time default.</summary>
        public static DateField CreateDateField(
            string name = "test_date",
            Guid? id = null,
            bool useCurrentTime = false,
            string format = "yyyy-MM-dd")
        {
            return new DateField
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Required = false,
                Unique = false,
                System = false,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                UseCurrentTimeAsDefaultValue = useCurrentTime,
                Format = format,
                Permissions = new FieldPermissions()
            };
        }

        /// <summary>Creates a DateTimeField with optional format and current-time default.</summary>
        public static DateTimeField CreateDateTimeField(
            string name = "test_datetime",
            Guid? id = null,
            bool useCurrentTime = false,
            string format = "yyyy-MM-dd HH:mm")
        {
            return new DateTimeField
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Required = false,
                Unique = false,
                System = false,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                UseCurrentTimeAsDefaultValue = useCurrentTime,
                Format = format,
                Permissions = new FieldPermissions()
            };
        }

        /// <summary>Creates a CheckboxField with optional default value.</summary>
        public static CheckboxField CreateCheckboxField(
            string name = "test_checkbox",
            Guid? id = null,
            bool defaultValue = false)
        {
            return new CheckboxField
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Required = false,
                Unique = false,
                System = false,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                DefaultValue = defaultValue,
                Permissions = new FieldPermissions()
            };
        }

        /// <summary>
        /// Creates a SelectField with optional default value and options list.
        /// If options is null, provides three default options.
        /// </summary>
        public static SelectField CreateSelectField(
            string name = "test_select",
            Guid? id = null,
            string? defaultValue = null,
            List<SelectOption>? options = null)
        {
            options ??= new List<SelectOption>
            {
                new SelectOption("option1", "Option 1"),
                new SelectOption("option2", "Option 2"),
                new SelectOption("option3", "Option 3")
            };

            return new SelectField
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Required = false,
                Unique = false,
                System = false,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                DefaultValue = defaultValue ?? string.Empty,
                Options = options,
                Permissions = new FieldPermissions()
            };
        }

        /// <summary>
        /// Creates a MultiSelectField with optional default values and options list.
        /// If options is null, provides three default options.
        /// </summary>
        public static MultiSelectField CreateMultiSelectField(
            string name = "test_multiselect",
            Guid? id = null,
            IEnumerable<string>? defaultValue = null,
            List<SelectOption>? options = null)
        {
            options ??= new List<SelectOption>
            {
                new SelectOption("option1", "Option 1"),
                new SelectOption("option2", "Option 2"),
                new SelectOption("option3", "Option 3")
            };

            return new MultiSelectField
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Required = false,
                Unique = false,
                System = false,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                DefaultValue = defaultValue ?? Enumerable.Empty<string>(),
                Options = options,
                Permissions = new FieldPermissions()
            };
        }

        /// <summary>Creates an EmailField with optional default and max length.</summary>
        public static EmailField CreateEmailField(
            string name = "test_email",
            Guid? id = null,
            string? defaultValue = "",
            int? maxLength = 320)
        {
            return new EmailField
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Required = false,
                Unique = false,
                System = false,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                DefaultValue = defaultValue,
                MaxLength = maxLength,
                Permissions = new FieldPermissions()
            };
        }

        /// <summary>Creates a PasswordField with optional length constraints and encryption toggle.</summary>
        public static PasswordField CreatePasswordField(
            string name = "test_password",
            Guid? id = null,
            int? maxLength = 24,
            int? minLength = 6,
            bool encrypted = true)
        {
            return new PasswordField
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Required = false,
                Unique = false,
                System = false,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                MaxLength = maxLength,
                MinLength = minLength,
                Encrypted = encrypted,
                Permissions = new FieldPermissions()
            };
        }

        /// <summary>Creates a CurrencyField with optional min/max and currency type.</summary>
        public static CurrencyField CreateCurrencyField(
            string name = "test_currency",
            Guid? id = null,
            decimal? defaultValue = 0,
            decimal? minValue = null,
            decimal? maxValue = null,
            CurrencyType? currency = null)
        {
            currency ??= new CurrencyType
            {
                Symbol = "$",
                SymbolNative = "$",
                Name = "US Dollar",
                NamePlural = "US dollars",
                Code = "USD",
                DecimalDigits = 2,
                Rounding = 0
            };

            return new CurrencyField
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Required = false,
                Unique = false,
                System = false,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                DefaultValue = defaultValue,
                MinValue = minValue,
                MaxValue = maxValue,
                Currency = currency,
                Permissions = new FieldPermissions()
            };
        }

        /// <summary>Creates an AutoNumberField with optional display format and starting number.</summary>
        public static AutoNumberField CreateAutoNumberField(
            string name = "test_autonumber",
            Guid? id = null,
            decimal? defaultValue = 0,
            string? displayFormat = null,
            decimal? startingNumber = 1)
        {
            return new AutoNumberField
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Required = false,
                Unique = false,
                System = false,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                DefaultValue = defaultValue,
                DisplayFormat = displayFormat ?? string.Empty,
                StartingNumber = startingNumber,
                Permissions = new FieldPermissions()
            };
        }

        /// <summary>Creates a PercentField with optional min/max and decimal places.</summary>
        public static PercentField CreatePercentField(
            string name = "test_percent",
            Guid? id = null,
            decimal? defaultValue = null,
            decimal? minValue = 0,
            decimal? maxValue = 100,
            byte? decimalPlaces = 2)
        {
            return new PercentField
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Required = false,
                Unique = false,
                System = false,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                DefaultValue = defaultValue,
                MinValue = minValue,
                MaxValue = maxValue,
                DecimalPlaces = decimalPlaces,
                Permissions = new FieldPermissions()
            };
        }

        /// <summary>Creates a PhoneField with optional format and max length.</summary>
        public static PhoneField CreatePhoneField(
            string name = "test_phone",
            Guid? id = null,
            string? defaultValue = "",
            string? format = null,
            int? maxLength = 30)
        {
            return new PhoneField
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Required = false,
                Unique = false,
                System = false,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                DefaultValue = defaultValue,
                Format = format,
                MaxLength = maxLength,
                Permissions = new FieldPermissions()
            };
        }

        /// <summary>Creates a UrlField with optional max length and target window setting.</summary>
        public static UrlField CreateUrlField(
            string name = "test_url",
            Guid? id = null,
            string? defaultValue = "",
            int? maxLength = 500,
            bool openTargetInNewWindow = false)
        {
            return new UrlField
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Required = false,
                Unique = false,
                System = false,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                DefaultValue = defaultValue,
                MaxLength = maxLength,
                OpenTargetInNewWindow = openTargetInNewWindow,
                Permissions = new FieldPermissions()
            };
        }

        /// <summary>Creates a FileField with optional default value for file path.</summary>
        public static FileField CreateFileField(
            string name = "test_file",
            Guid? id = null,
            string defaultValue = "")
        {
            return new FileField
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Required = false,
                Unique = false,
                System = false,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                DefaultValue = defaultValue,
                Permissions = new FieldPermissions()
            };
        }

        /// <summary>Creates an ImageField with optional default value for image path.</summary>
        public static ImageField CreateImageField(
            string name = "test_image",
            Guid? id = null,
            string? defaultValue = "")
        {
            return new ImageField
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Required = false,
                Unique = false,
                System = false,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                DefaultValue = defaultValue,
                Permissions = new FieldPermissions()
            };
        }

        /// <summary>Creates an HtmlField with optional default HTML content.</summary>
        public static HtmlField CreateHtmlField(
            string name = "test_html",
            Guid? id = null,
            string? defaultValue = "")
        {
            return new HtmlField
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Required = false,
                Unique = false,
                System = false,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                DefaultValue = defaultValue,
                Permissions = new FieldPermissions()
            };
        }

        /// <summary>Creates a MultiLineTextField with optional max length and visible lines.</summary>
        public static MultiLineTextField CreateMultiLineTextField(
            string name = "test_multiline",
            Guid? id = null,
            string defaultValue = "",
            int? maxLength = null,
            int? visibleLineNumber = 4)
        {
            return new MultiLineTextField
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Required = false,
                Unique = false,
                System = false,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                DefaultValue = defaultValue,
                MaxLength = maxLength,
                VisibleLineNumber = visibleLineNumber,
                Permissions = new FieldPermissions()
            };
        }

        /// <summary>Creates a GeographyField with optional default value and SRID.</summary>
        public static GeographyField CreateGeographyField(
            string name = "test_geography",
            Guid? id = null,
            string? defaultValue = null,
            int srid = 4326)
        {
            return new GeographyField
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Required = false,
                Unique = false,
                System = false,
                Searchable = false,
                Auditable = false,
                EnableSecurity = false,
                DefaultValue = defaultValue ?? string.Empty,
                SRID = srid,
                Permissions = new FieldPermissions()
            };
        }

        // =====================================================================
        // Phase 4: Relation Builder Methods
        // =====================================================================

        /// <summary>Creates a OneToOne relation between two entities.</summary>
        public static EntityRelation CreateOneToOneRelation(
            string name = "test_one_to_one",
            Guid? id = null,
            Guid? originEntityId = null,
            Guid? originFieldId = null,
            Guid? targetEntityId = null,
            Guid? targetFieldId = null)
        {
            return new EntityRelation
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Description = $"One-to-one relation: {name}",
                System = false,
                RelationType = EntityRelationType.OneToOne,
                OriginEntityId = originEntityId ?? Guid.NewGuid(),
                OriginFieldId = originFieldId ?? Guid.NewGuid(),
                TargetEntityId = targetEntityId ?? Guid.NewGuid(),
                TargetFieldId = targetFieldId ?? Guid.NewGuid()
            };
        }

        /// <summary>Creates a OneToMany relation between two entities.</summary>
        public static EntityRelation CreateOneToManyRelation(
            string name = "test_one_to_many",
            Guid? id = null,
            Guid? originEntityId = null,
            Guid? originFieldId = null,
            Guid? targetEntityId = null,
            Guid? targetFieldId = null)
        {
            return new EntityRelation
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Description = $"One-to-many relation: {name}",
                System = false,
                RelationType = EntityRelationType.OneToMany,
                OriginEntityId = originEntityId ?? Guid.NewGuid(),
                OriginFieldId = originFieldId ?? Guid.NewGuid(),
                TargetEntityId = targetEntityId ?? Guid.NewGuid(),
                TargetFieldId = targetFieldId ?? Guid.NewGuid()
            };
        }

        /// <summary>Creates a ManyToMany relation between two entities.</summary>
        public static EntityRelation CreateManyToManyRelation(
            string name = "test_many_to_many",
            Guid? id = null,
            Guid? originEntityId = null,
            Guid? originFieldId = null,
            Guid? targetEntityId = null,
            Guid? targetFieldId = null)
        {
            return new EntityRelation
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = ToLabel(name),
                Description = $"Many-to-many relation: {name}",
                System = false,
                RelationType = EntityRelationType.ManyToMany,
                OriginEntityId = originEntityId ?? Guid.NewGuid(),
                OriginFieldId = originFieldId ?? Guid.NewGuid(),
                TargetEntityId = targetEntityId ?? Guid.NewGuid(),
                TargetFieldId = targetFieldId ?? Guid.NewGuid()
            };
        }

        /// <summary>
        /// Creates the standard user_{entityName}_created_by relation linking
        /// the User entity to a target entity. Mirrors DbEntityRepository.cs
        /// auto-created relations (OneToMany from User to target entity).
        /// </summary>
        /// <param name="entityName">Target entity programmatic name.</param>
        /// <returns>A OneToMany relation from UserEntity to the named entity.</returns>
        public static EntityRelation CreateUserEntityRelation(string entityName)
        {
            return new EntityRelation
            {
                Id = Guid.NewGuid(),
                Name = $"user_{entityName}_created_by",
                Label = $"User {entityName} Created By",
                Description = $"Standard created_by relation for {entityName} entity",
                System = false,
                RelationType = EntityRelationType.OneToMany,
                OriginEntityId = SystemIds.UserEntityId,
                OriginFieldId = Guid.NewGuid(),
                TargetEntityId = Guid.NewGuid(),
                TargetFieldId = Guid.NewGuid(),
                OriginEntityName = "user",
                OriginFieldName = "id",
                TargetEntityName = entityName,
                TargetFieldName = "created_by"
            };
        }

        // =====================================================================
        // Phase 5: Record Builder Methods
        // =====================================================================

        /// <summary>
        /// Creates a minimal test record with standard system fields (id, created_on,
        /// created_by, last_modified_on, last_modified_by).
        /// </summary>
        /// <param name="id">Optional record ID; generates new if null.</param>
        /// <param name="createdBy">Optional creator user ID; defaults to SystemUserId.</param>
        /// <returns>EntityRecord with standard fields populated.</returns>
        public static EntityRecord CreateTestRecord(Guid? id = null, Guid? createdBy = null)
        {
            var userId = createdBy ?? SystemIds.SystemUserId;
            var now = DateTime.UtcNow;

            var record = new EntityRecord();
            record["id"] = id ?? Guid.NewGuid();
            record["created_on"] = now;
            record["created_by"] = userId;
            record["last_modified_on"] = now;
            record["last_modified_by"] = userId;

            return record;
        }

        /// <summary>
        /// Creates a test record with all entity fields populated. System fields
        /// (id, created_on, created_by, last_modified_on, last_modified_by) are
        /// auto-populated; custom fields get values from fieldValues or type-appropriate
        /// defaults.
        /// </summary>
        /// <param name="entity">Entity definition whose fields define the record shape.</param>
        /// <param name="fieldValues">Optional field value overrides keyed by field name.</param>
        /// <returns>EntityRecord with all fields populated.</returns>
        public static EntityRecord CreateTestRecordWithFields(
            Entity entity,
            Dictionary<string, object>? fieldValues = null)
        {
            var record = CreateTestRecord();
            var systemFieldNames = new HashSet<string>
            {
                "id", "created_on", "created_by", "last_modified_on", "last_modified_by"
            };

            foreach (var field in entity.Fields.Where(f => !systemFieldNames.Contains(f.Name)))
            {
                if (fieldValues != null && fieldValues.TryGetValue(field.Name, out var customValue))
                {
                    record[field.Name] = customValue;
                }
                else
                {
                    record[field.Name] = GetDefaultTestValue(field);
                }
            }

            return record;
        }

        /// <summary>
        /// Creates a batch of test records with unique IDs. If an entity is provided,
        /// each record's fields are populated using CreateTestRecordWithFields with
        /// incrementally varied default values.
        /// </summary>
        /// <param name="count">Number of records to generate.</param>
        /// <param name="entity">Optional entity definition for field-populated records.</param>
        /// <returns>List of EntityRecords with unique IDs.</returns>
        public static List<EntityRecord> CreateTestRecordBatch(int count, Entity? entity = null)
        {
            var records = new List<EntityRecord>(count);

            for (int i = 0; i < count; i++)
            {
                if (entity != null)
                {
                    var overrides = new Dictionary<string, object>();

                    // Provide incrementally varied values for non-system fields
                    foreach (var field in entity.Fields.Where(f =>
                        f.Name != "id" && f.Name != "created_on" && f.Name != "created_by" &&
                        f.Name != "last_modified_on" && f.Name != "last_modified_by"))
                    {
                        overrides[field.Name] = GetIncrementedTestValue(field, i);
                    }

                    records.Add(CreateTestRecordWithFields(entity, overrides));
                }
                else
                {
                    records.Add(CreateTestRecord());
                }
            }

            return records;
        }

        /// <summary>
        /// Returns a sensible default test value for a given field based on its type.
        /// Used by CreateTestRecordWithFields when no explicit value is provided.
        /// </summary>
        private static object? GetDefaultTestValue(Field field)
        {
            var fieldType = field.GetFieldType();
            return fieldType switch
            {
                FieldType.TextField => "test_value",
                FieldType.EmailField => "test@example.com",
                FieldType.PhoneField => "+1234567890",
                FieldType.UrlField => "https://example.com",
                FieldType.NumberField => 0m,
                FieldType.CurrencyField => 0m,
                FieldType.PercentField => 0m,
                FieldType.CheckboxField => false,
                FieldType.DateField => DateTime.UtcNow.Date,
                FieldType.DateTimeField => DateTime.UtcNow,
                FieldType.GuidField => Guid.NewGuid(),
                FieldType.SelectField => (field is SelectField sf && sf.Options?.Count > 0)
                    ? sf.Options[0].Value
                    : (object?)null,
                FieldType.MultiSelectField => new List<string>(),
                FieldType.PasswordField => "TestPassword123!",
                FieldType.AutoNumberField => 1m,
                FieldType.FileField => "/path/to/test-file",
                FieldType.ImageField => "/path/to/test-image.png",
                FieldType.HtmlField => "<p>test</p>",
                FieldType.MultiLineTextField => "Line 1\nLine 2",
                FieldType.GeographyField => "POINT(0 0)",
                _ => null
            };
        }

        /// <summary>
        /// Returns an incrementally varied test value for batch generation.
        /// Appends or adds the index to differentiate records in a batch.
        /// </summary>
        private static object GetIncrementedTestValue(Field field, int index)
        {
            var fieldType = field.GetFieldType();
            return fieldType switch
            {
                FieldType.TextField => $"test_value_{index}",
                FieldType.EmailField => $"test{index}@example.com",
                FieldType.PhoneField => $"+123456789{index}",
                FieldType.UrlField => $"https://example.com/{index}",
                FieldType.NumberField => (decimal)index,
                FieldType.CurrencyField => (decimal)(index * 10),
                FieldType.PercentField => (decimal)(index * 5),
                FieldType.CheckboxField => (index % 2 == 0),
                FieldType.DateField => DateTime.UtcNow.Date.AddDays(index),
                FieldType.DateTimeField => DateTime.UtcNow.AddHours(index),
                FieldType.GuidField => Guid.NewGuid(),
                FieldType.SelectField => (field is SelectField sf && sf.Options?.Count > 0)
                    ? sf.Options[index % sf.Options.Count].Value
                    : $"option_{index}",
                FieldType.MultiSelectField => new List<string> { $"option_{index}" },
                FieldType.PasswordField => $"TestPassword{index}!",
                FieldType.AutoNumberField => (decimal)(index + 1),
                FieldType.FileField => $"/path/to/test-file-{index}",
                FieldType.ImageField => $"/path/to/test-image-{index}.png",
                FieldType.HtmlField => $"<p>test content {index}</p>",
                FieldType.MultiLineTextField => $"Line 1 record {index}\nLine 2 record {index}",
                FieldType.GeographyField => $"POINT({index} {index})",
                _ => $"value_{index}"
            };
        }

        // =====================================================================
        // Phase 6: CSV Test Data Generators
        // =====================================================================

        /// <summary>
        /// Generates CSV content with header and data rows for the given entity.
        /// Produces comma-separated values matching CsvHelper output format.
        /// </summary>
        /// <param name="entity">Entity definition whose fields define the CSV columns.</param>
        /// <param name="rowCount">Number of data rows to generate (default: 5).</param>
        /// <returns>Complete CSV string with header and data rows.</returns>
        public static string GenerateTestCsvContent(Entity entity, int rowCount = 5)
        {
            var sb = new StringBuilder();
            var fields = entity.Fields.ToList();

            // Header row
            sb.AppendLine(string.Join(",", fields.Select(f => EscapeCsvField(f.Name))));

            // Data rows
            for (int i = 0; i < rowCount; i++)
            {
                var values = new List<string>();
                foreach (var field in fields)
                {
                    var value = GetIncrementedTestValue(field, i);
                    values.Add(EscapeCsvField(ConvertToCsvString(value)));
                }
                sb.AppendLine(string.Join(",", values));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Generates CSV content as a UTF-8 byte array, ready for stream-based processing.
        /// </summary>
        /// <param name="entity">Entity definition whose fields define the CSV columns.</param>
        /// <param name="rowCount">Number of data rows to generate (default: 5).</param>
        /// <returns>UTF-8 encoded byte array of the CSV content.</returns>
        public static byte[] GenerateTestCsvBytes(Entity entity, int rowCount = 5)
        {
            return Encoding.UTF8.GetBytes(GenerateTestCsvContent(entity, rowCount));
        }

        /// <summary>Escapes a CSV field value by quoting if it contains commas, quotes, or newlines.</summary>
        private static string EscapeCsvField(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            return value;
        }

        /// <summary>Converts a field value object to its CSV string representation.</summary>
        private static string ConvertToCsvString(object? value)
        {
            if (value == null) return string.Empty;
            if (value is DateTime dt) return dt.ToString("yyyy-MM-dd HH:mm:ss");
            if (value is Guid g) return g.ToString();
            if (value is bool b) return b ? "true" : "false";
            if (value is decimal d) return d.ToString("G");
            if (value is IEnumerable<string> list) return string.Join("|", list);
            return value.ToString() ?? string.Empty;
        }

        // =====================================================================
        // Phase 7: DynamoDB Item Builders (for direct table seeding)
        // =====================================================================

        /// <summary>
        /// Builds a DynamoDB item for entity metadata using the single-table design.
        /// PK=ENTITY#{entityId}, SK=META, with GSI1 for name-based lookups.
        /// </summary>
        /// <param name="entity">Entity definition to serialize into the item.</param>
        /// <returns>DynamoDB item dictionary with AttributeValue entries.</returns>
        public static Dictionary<string, AttributeValue> CreateEntityMetadataItem(Entity entity)
        {
            // Attribute names and GSI1PK value MUST match EntityRepository constants:
            // ENTITY_DATA_ATTR = "entityData", GSI1PK uses ToLowerInvariant()
            var settings = new Newtonsoft.Json.JsonSerializerSettings
            {
                TypeNameHandling = Newtonsoft.Json.TypeNameHandling.Auto,
                NullValueHandling = Newtonsoft.Json.NullValueHandling.Include,
                Formatting = Newtonsoft.Json.Formatting.None
            };
            return new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"ENTITY#{entity.Id}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["entityData"] = new AttributeValue { S = Newtonsoft.Json.JsonConvert.SerializeObject(entity, settings) },
                ["GSI1PK"] = new AttributeValue { S = $"ENTITY_NAME#{entity.Name.ToLowerInvariant()}" },
                ["GSI1SK"] = new AttributeValue { S = "META" }
            };
        }

        /// <summary>
        /// Builds a DynamoDB item for field metadata under its parent entity.
        /// PK=ENTITY#{entityId}, SK=FIELD#{fieldId}.
        /// </summary>
        /// <param name="entityId">Parent entity identifier.</param>
        /// <param name="field">Field definition to serialize into the item.</param>
        /// <returns>DynamoDB item dictionary with AttributeValue entries.</returns>
        public static Dictionary<string, AttributeValue> CreateFieldMetadataItem(Guid entityId, Field field)
        {
            // Attribute name MUST match EntityRepository FIELD_DATA_ATTR = "fieldData"
            // Serialize with TypeNameHandling.Auto for polymorphic deserialization
            var settings = new Newtonsoft.Json.JsonSerializerSettings
            {
                TypeNameHandling = Newtonsoft.Json.TypeNameHandling.Auto,
                NullValueHandling = Newtonsoft.Json.NullValueHandling.Include,
                Formatting = Newtonsoft.Json.Formatting.None
            };
            return new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"ENTITY#{entityId}" },
                ["SK"] = new AttributeValue { S = $"FIELD#{field.Id}" },
                ["fieldData"] = new AttributeValue { S = Newtonsoft.Json.JsonConvert.SerializeObject(field, field.GetType(), settings) }
            };
        }

        /// <summary>
        /// Builds a DynamoDB item for relation metadata.
        /// PK=RELATION#{relationId}, SK=META.
        /// </summary>
        /// <param name="relation">Relation definition to serialize into the item.</param>
        /// <returns>DynamoDB item dictionary with AttributeValue entries.</returns>
        public static Dictionary<string, AttributeValue> CreateRelationMetadataItem(EntityRelation relation)
        {
            // Attribute name MUST match EntityRepository RELATION_DATA_ATTR = "relationData"
            // PK pattern: ENTITY#{originEntityId}, SK: RELATION#{relationId}
            // Plus GSI2 for global relation lookup: GSI2PK=RELATION#{relationId}, GSI2SK=META
            var settings = new Newtonsoft.Json.JsonSerializerSettings
            {
                TypeNameHandling = Newtonsoft.Json.TypeNameHandling.Auto,
                NullValueHandling = Newtonsoft.Json.NullValueHandling.Include,
                Formatting = Newtonsoft.Json.Formatting.None
            };
            return new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"ENTITY#{relation.OriginEntityId}" },
                ["SK"] = new AttributeValue { S = $"RELATION#{relation.Id}" },
                ["relationData"] = new AttributeValue { S = Newtonsoft.Json.JsonConvert.SerializeObject(relation, settings) },
                ["GSI2PK"] = new AttributeValue { S = $"RELATION#{relation.Id}" },
                ["GSI2SK"] = new AttributeValue { S = "META" }
            };
        }

        /// <summary>
        /// Builds a DynamoDB item for a record under its entity's partition.
        /// PK=ENTITY#{entityName}, SK=RECORD#{recordId}, with GSI1 for chronological queries.
        /// </summary>
        /// <param name="entityName">Entity name for the record's partition key.</param>
        /// <param name="record">Record data to serialize into the item.</param>
        /// <returns>DynamoDB item dictionary with AttributeValue entries.</returns>
        public static Dictionary<string, AttributeValue> CreateRecordItem(string entityName, EntityRecord record)
        {
            var recordId = record["id"]?.ToString() ?? Guid.NewGuid().ToString();
            var createdOn = record.ContainsKey("created_on") && record["created_on"] is DateTime dt
                ? dt.ToString("o")
                : DateTime.UtcNow.ToString("o");

            return new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"ENTITY#{entityName}" },
                ["SK"] = new AttributeValue { S = $"RECORD#{recordId}" },
                ["RecordJson"] = new AttributeValue { S = JsonSerializer.Serialize(record) },
                ["GSI1PK"] = new AttributeValue { S = $"ENTITY#{entityName}" },
                ["GSI1SK"] = new AttributeValue { S = $"CREATED#{createdOn}" }
            };
        }
    }
}
