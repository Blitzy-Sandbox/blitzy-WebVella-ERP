using System;
using System.Text.Json.Serialization;

namespace WebVellaErp.Crm.Models
{
    /// <summary>
    /// Represents a CRM account entity in the CRM bounded-context microservice.
    /// Extracted from the monolith's entity patch definitions in NextPlugin.20190203,
    /// NextPlugin.20190204, and NextPlugin.20190206. Supports both Company and Person
    /// account types with comprehensive contact, address, and classification fields.
    ///
    /// Entity ID: 2e22b50f-e444-4b62-a171-076e51246939
    /// DynamoDB key pattern: PK=ENTITY#account, SK=RECORD#{Id}
    ///
    /// All properties use <see cref="JsonPropertyNameAttribute"/> with snake_case names
    /// matching the original entity patch field names for DynamoDB attribute compatibility
    /// and System.Text.Json Native AOT serialization (NOT Newtonsoft.Json).
    /// </summary>
    public class Account
    {
        /// <summary>
        /// The entity definition identifier for the Account entity.
        /// Source: NextPlugin.20190203.cs line 985 — entity.Id = new Guid("2e22b50f-e444-4b62-a171-076e51246939").
        /// Used for DynamoDB partition key construction and cross-service entity reference.
        /// </summary>
        public static readonly Guid EntityId = new Guid("2e22b50f-e444-4b62-a171-076e51246939");

        /// <summary>
        /// Default salutation identifier assigned to new accounts when no salutation is specified.
        /// Source: NextPlugin.20190206.cs line 131 — guidField.DefaultValue = Guid.Parse("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698").
        /// Represents the default salutation record in the salutation entity table.
        /// </summary>
        public static readonly Guid DefaultSalutationId = new Guid("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698");

        /// <summary>
        /// Initializes a new instance of the <see cref="Account"/> class with required string
        /// fields set to safe defaults to prevent null reference issues when deserializing
        /// from DynamoDB or constructing new account records. Required non-nullable fields
        /// are initialized to match their original entity patch default values.
        /// </summary>
        public Account()
        {
            Id = Guid.Empty;
            Name = string.Empty;
            Type = AccountType.Company;
            LastName = string.Empty;
            FirstName = string.Empty;
            XSearch = string.Empty;
            LScope = string.Empty;
            SalutationId = DefaultSalutationId;
            CreatedOn = DateTime.UtcNow;
        }

        // ======================================================================
        // Core/System Fields (from NextPlugin.20190203.cs lines 978-1077)
        // ======================================================================

        /// <summary>
        /// Unique record identifier. System auto-generated GUID field.
        /// Source: NextPlugin.20190203.cs line 984 — systemFieldIdDictionary["id"] =
        /// new Guid("4c0c80d0-8b01-445f-9913-0be18d9086d1").
        /// Used as the DynamoDB sort key component: SK=RECORD#{Id}.
        /// </summary>
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Display name of the account. Required field used as the record screen identifier.
        /// Source: NextPlugin.20190203.cs lines 1019-1047 — InputTextField, Required=true,
        /// System=true, DefaultValue="name". Field ID: b8be9afb-687c-411a-a274-ebe5d36a8100.
        /// Also serves as RecordScreenIdField for the entity definition.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        // ======================================================================
        // CRM Fields (from NextPlugin.20190204.cs lines 16-623)
        // ======================================================================

        /// <summary>
        /// Account type classification. Required select field with predefined options.
        /// Values: "1" (Company), "2" (Person). See <see cref="AccountType"/> for constants.
        /// Source: NextPlugin.20190204.cs lines 16-48 — InputSelectField, Required=true,
        /// System=true, Searchable=true, DefaultValue="1".
        /// Field ID: 7cab7793-1ae4-4c05-9191-4035a0d54bd1.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; }

        /// <summary>
        /// Account website URL. Optional URL field.
        /// Source: NextPlugin.20190204.cs lines 50-79 — InputUrlField, Required=false,
        /// System=true, OpenTargetInNewWindow=false.
        /// Field ID: df7114b5-49ad-400b-ae16-a6ed1daa8a0c.
        /// </summary>
        [JsonPropertyName("website")]
        public string? Website { get; set; }

        /// <summary>
        /// Primary street address line. Optional text field.
        /// Source: NextPlugin.20190204.cs lines 81-109 — InputTextField, Required=false, System=true.
        /// Field ID: 1bc1ead8-2673-4cdd-b0f3-b99d4cf4fadc.
        /// </summary>
        [JsonPropertyName("street")]
        public string? Street { get; set; }

        /// <summary>
        /// Address region, state, or province. Optional text field.
        /// Source: NextPlugin.20190204.cs lines 111-139 — InputTextField, Required=false, System=true.
        /// Field ID: 9c29b56d-2db2-47c6-bcf6-96cbe7187119.
        /// </summary>
        [JsonPropertyName("region")]
        public string? Region { get; set; }

        /// <summary>
        /// Postal code or ZIP code. Optional text field.
        /// Source: NextPlugin.20190204.cs lines 141-169 — InputTextField, Required=false, System=true.
        /// Field ID: caaaf464-67b7-47b2-afec-beec03d90e4f.
        /// </summary>
        [JsonPropertyName("post_code")]
        public string? PostCode { get; set; }

        /// <summary>
        /// Fixed/landline telephone number. Optional phone field.
        /// Source: NextPlugin.20190204.cs lines 171-200 — InputPhoneField, Required=false, System=true.
        /// Field ID: f51f7451-b9f1-4a5a-a282-3d83525a9094.
        /// </summary>
        [JsonPropertyName("fixed_phone")]
        public string? FixedPhone { get; set; }

        /// <summary>
        /// Mobile/cellular telephone number. Optional phone field.
        /// Source: NextPlugin.20190204.cs lines 202-231 — InputPhoneField, Required=false, System=true.
        /// Field ID: 01e8d8e6-457b-49c8-9194-81f06bd9f8ed.
        /// </summary>
        [JsonPropertyName("mobile_phone")]
        public string? MobilePhone { get; set; }

        /// <summary>
        /// Facsimile (fax) telephone number. Optional phone field.
        /// Source: NextPlugin.20190204.cs lines 233-262 — InputPhoneField, Required=false, System=true.
        /// Field ID: 8f6bbfac-8f10-4023-b2b0-af03d22b9cef.
        /// </summary>
        [JsonPropertyName("fax_phone")]
        public string? FaxPhone { get; set; }

        /// <summary>
        /// Free-text notes and additional information about the account. Optional multi-line text field.
        /// Source: NextPlugin.20190204.cs lines 264-293 — InputMultiLineTextField, Required=false, System=true.
        /// Field ID: d2c7a984-c173-434f-a711-1f1efa07f0c1.
        /// </summary>
        [JsonPropertyName("notes")]
        public string? Notes { get; set; }

        /// <summary>
        /// Contact's last name (surname). Required for both Company and Person account types.
        /// Source: NextPlugin.20190204.cs lines 295-323 — InputTextField, Required=true,
        /// System=true, DefaultValue="last name".
        /// Field ID: c9da8e17-9511-4f2c-8576-8756f34a17b9.
        /// </summary>
        [JsonPropertyName("last_name")]
        public string LastName { get; set; }

        /// <summary>
        /// Contact's first name (given name). Required for both Company and Person account types.
        /// Source: NextPlugin.20190204.cs lines 325-353 — InputTextField, Required=true,
        /// System=true, DefaultValue="first name".
        /// Field ID: 66de2df4-f42a-4bc9-817d-8960578a8302.
        /// </summary>
        [JsonPropertyName("first_name")]
        public string FirstName { get; set; }

        /// <summary>
        /// Composite search index field for full-text search capability in DynamoDB.
        /// Contains concatenated searchable field values (name, email, phone, etc.) to enable
        /// efficient keyword searches using DynamoDB string contains operations or GSI queries.
        /// Initially created in NextPlugin.20190204.cs (lines 355-383) as optional, then updated
        /// in NextPlugin.20190206.cs (lines 147-176) to Required=true, DefaultValue="",
        /// Label="Search Index", PlaceholderText="search accounts", Searchable=true.
        /// Field ID: d8ce135d-f6c4-45b7-a543-c58e154c06df.
        /// </summary>
        [JsonPropertyName("x_search")]
        public string XSearch { get; set; }

        /// <summary>
        /// Primary email address for the account. Optional field with search index support.
        /// Source: NextPlugin.20190204.cs lines 385-413 — InputEmailField, Required=false,
        /// System=true, Searchable=true.
        /// Field ID: 25dcf767-2e12-4413-b096-60d37700194f.
        /// </summary>
        [JsonPropertyName("email")]
        public string? Email { get; set; }

        /// <summary>
        /// City name for the account's primary address. Optional text field.
        /// Source: NextPlugin.20190204.cs lines 415-443 — InputTextField, Required=false, System=true.
        /// Field ID: 4e18d041-0daf-4db4-9bd9-6d5b631af0bd.
        /// </summary>
        [JsonPropertyName("city")]
        public string? City { get; set; }

        /// <summary>
        /// Reference to the country entity record. Optional GUID foreign key field.
        /// Source: NextPlugin.20190204.cs lines 445-473 — InputGuidField, Required=false,
        /// System=true, GenerateNewId=false.
        /// Field ID: 76c1d754-8bf5-4a78-a2d7-bf771e1b032b.
        /// </summary>
        [JsonPropertyName("country_id")]
        public Guid? CountryId { get; set; }

        /// <summary>
        /// Tax identification number (VAT, EIN, TIN, etc.). Optional text field.
        /// Source: NextPlugin.20190204.cs lines 475-503 — InputTextField, Required=false, System=true.
        /// Field ID: c4bbc47c-2dc0-4c24-9159-1b5a6bfa8ed3.
        /// </summary>
        [JsonPropertyName("tax_id")]
        public string? TaxId { get; set; }

        /// <summary>
        /// Secondary street address line (suite, building, floor, etc.). Optional text field.
        /// Source: NextPlugin.20190204.cs lines 535-563 — InputTextField, Required=false, System=true.
        /// Field ID: 8829ff72-2910-40a8-834d-5f05c51c8d2f.
        /// </summary>
        [JsonPropertyName("street_2")]
        public string? Street2 { get; set; }

        /// <summary>
        /// Reference to the language entity record for the account's preferred language.
        /// Optional GUID foreign key field.
        /// Source: NextPlugin.20190204.cs lines 565-593 — InputGuidField, Required=false,
        /// System=true, GenerateNewId=false.
        /// Field ID: 02b796b4-2b7a-4662-8a16-01dbffdd1ba1.
        /// </summary>
        [JsonPropertyName("language_id")]
        public Guid? LanguageId { get; set; }

        /// <summary>
        /// Reference to the currency entity record for the account's preferred currency.
        /// Optional GUID foreign key field.
        /// Source: NextPlugin.20190204.cs lines 595-623 — InputGuidField, Required=false,
        /// System=true, GenerateNewId=false.
        /// Field ID: c2a2a490-951d-4395-b359-0dc88ad56c11.
        /// </summary>
        [JsonPropertyName("currency_id")]
        public Guid? CurrencyId { get; set; }

        // ======================================================================
        // Fields Added by NextPlugin.20190206.cs
        // ======================================================================

        /// <summary>
        /// Record creation timestamp. Required field with automatic current time initialization.
        /// Source: NextPlugin.20190206.cs lines 86-115 — InputDateTimeField, Required=true,
        /// System=true, Format="yyyy-MMM-dd HH:mm", UseCurrentTimeAsDefaultValue=true.
        /// Field ID: 48a33ffe-d5e4-4fa1-b74c-272733201652.
        /// </summary>
        [JsonPropertyName("created_on")]
        public DateTime CreatedOn { get; set; }

        /// <summary>
        /// Reference to the salutation entity record (Mr., Mrs., Dr., etc.). Required field
        /// with a default value pointing to the standard salutation record.
        /// Default: 87c08ee1-8d4d-4c89-9b37-4e3cc3f98698 (see <see cref="DefaultSalutationId"/>).
        /// Replaces the misspelled "solutation_id" field from NextPlugin.20190204.cs (lines 505-533)
        /// which was deleted in NextPlugin.20190206.cs (lines 35-43) and recreated with the
        /// correct spelling.
        /// Source: NextPlugin.20190206.cs lines 117-145 — InputGuidField, Required=true,
        /// System=true, GenerateNewId=false.
        /// Field ID: dce30f5b-7c87-450e-a60a-757f758d9f62.
        /// </summary>
        [JsonPropertyName("salutation_id")]
        public Guid SalutationId { get; set; }

        /// <summary>
        /// Profile photo or avatar image URL/path for the account. Optional image field.
        /// Stores a reference to an uploaded image in S3 (via the File Management service)
        /// or a URL string for externally hosted images. Included per AAP specification
        /// for CRM account profile images.
        /// </summary>
        [JsonPropertyName("photo")]
        public string? Photo { get; set; }

        // ======================================================================
        // Scope Field (from NextPlugin.20190203.cs, updated in NextPlugin.20190206.cs)
        // ======================================================================

        /// <summary>
        /// Scope identifier for multi-tenancy, data partitioning, or access control filtering.
        /// Initially created in NextPlugin.20190203.cs (lines 1049-1077) with Required=false,
        /// Searchable=false, DefaultValue=null. Updated in NextPlugin.20190206.cs (lines 178-207)
        /// to Required=true, Searchable=true, DefaultValue="". Used in DynamoDB GSI for
        /// scope-based query filtering.
        /// Field ID: fda3238e-52b5-48b7-82ad-558573c6e25c.
        /// </summary>
        [JsonPropertyName("l_scope")]
        public string LScope { get; set; }
    }

    /// <summary>
    /// Defines the available account type classifications used by the <see cref="Account.Type"/>
    /// select field. Values mirror the SelectOption list from NextPlugin.20190204.cs lines 31-35
    /// where Company (value "1") and Person (value "2") were defined as the only valid options
    /// for the account type dropdown field.
    /// </summary>
    public static class AccountType
    {
        /// <summary>
        /// Account type value representing a company or organization entity (value "1").
        /// This is the default account type (DefaultValue="1" in the source entity patch).
        /// </summary>
        public const string Company = "1";

        /// <summary>
        /// Account type value representing an individual person (value "2").
        /// Used when the account represents a natural person rather than a legal entity.
        /// </summary>
        public const string Person = "2";
    }
}
