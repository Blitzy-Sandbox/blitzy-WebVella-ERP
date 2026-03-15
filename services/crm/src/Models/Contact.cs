using System;
using System.Text.Json.Serialization;

namespace WebVellaErp.Crm.Models
{
    /// <summary>
    /// Represents a CRM contact entity in the CRM bounded-context microservice.
    /// Extracted from the monolith's entity patch definitions in
    /// <c>WebVella.Erp.Plugins.Next/NextPlugin.20190204.cs</c> (contact entity creation, lines 1401–1895)
    /// and <c>WebVella.Erp.Plugins.Next/NextPlugin.20190206.cs</c> (field corrections and additions, lines 429–547).
    /// 
    /// This model serves as the data contract for the CRM service's DynamoDB single-table
    /// persistence layer and HTTP API Gateway v2 JSON responses. All properties use
    /// <see cref="JsonPropertyNameAttribute"/> with snake_case names matching the original
    /// entity field names, ensuring backward-compatible serialization with existing data
    /// and API consumers.
    /// 
    /// <para><b>Serialization:</b> Uses <c>System.Text.Json</c> exclusively (NOT Newtonsoft.Json)
    /// for .NET 9 Native AOT compatibility, targeting Lambda cold start &lt; 1 second.</para>
    /// 
    /// <para><b>DynamoDB Key Design:</b>
    /// <list type="bullet">
    ///   <item>PK: <c>ENTITY#contact</c></item>
    ///   <item>SK: <c>RECORD#{Id}</c></item>
    /// </list>
    /// </para>
    /// </summary>
    public class Contact
    {
        #region Constants

        /// <summary>
        /// The unique entity identifier for the Contact entity type.
        /// Extracted from <c>NextPlugin.20190204.cs</c> line 1408:
        /// <c>entity.Id = new Guid("39e1dd9b-827f-464d-95ea-507ade81cbd0")</c>.
        /// Used for entity metadata lookups and DynamoDB partition key construction.
        /// </summary>
        public static readonly Guid EntityId = new Guid("39e1dd9b-827f-464d-95ea-507ade81cbd0");

        /// <summary>
        /// The default salutation identifier applied to new contacts when no explicit
        /// salutation is specified. Extracted from <c>NextPlugin.20190206.cs</c> line 533:
        /// <c>guidField.DefaultValue = Guid.Parse("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698")</c>.
        /// This matches the default salutation record seeded by the Next plugin.
        /// </summary>
        public static readonly Guid DefaultSalutationId = new Guid("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698");

        #endregion

        #region Record Permission Reference Constants

        // Record-level CRUD permissions extracted from NextPlugin.20190204.cs (lines 1416–1432)
        // and confirmed in NextPlugin.20190206.cs (lines 397–427).
        //
        // Regular Role: f16ec6db-626d-4c27-8de0-3e7ce542c55f — CanCreate, CanRead, CanUpdate, CanDelete
        // Admin Role:   bdc56420-caf0-4030-8a0e-d264938e0cda — CanCreate, CanRead, CanUpdate, CanDelete
        //
        // These are documented here for reference. In the serverless architecture, permission
        // enforcement is handled by the Identity service (Cognito groups + Lambda authorizer)
        // and the CRM service's PermissionService validation layer.

        /// <summary>
        /// Role identifier for the Regular role that has full CRUD permissions on contact records.
        /// </summary>
        internal static readonly Guid RegularRoleId = new Guid("f16ec6db-626d-4c27-8de0-3e7ce542c55f");

        /// <summary>
        /// Role identifier for the Administrator role that has full CRUD permissions on contact records.
        /// </summary>
        internal static readonly Guid AdministratorRoleId = new Guid("bdc56420-caf0-4030-8a0e-d264938e0cda");

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="Contact"/> class with safe defaults
        /// for all required fields. Required string properties are initialized to
        /// <see cref="string.Empty"/> to prevent null reference issues during deserialization
        /// and construction. The <see cref="SalutationId"/> defaults to
        /// <see cref="DefaultSalutationId"/> and <see cref="CreatedOn"/> defaults to
        /// <see cref="DateTime.UtcNow"/>.
        /// </summary>
        public Contact()
        {
            Id = Guid.Empty;
            FirstName = string.Empty;
            LastName = string.Empty;
            SalutationId = DefaultSalutationId;
            CreatedOn = DateTime.UtcNow;
            XSearch = string.Empty;
        }

        #endregion

        #region System Fields

        /// <summary>
        /// The unique record identifier for this contact.
        /// System auto-generated field (system field ID: <c>859f24ec-4d3e-4597-9972-1d5a9cba918b</c>).
        /// Extracted from <c>NextPlugin.20190204.cs</c> line 1407.
        /// </summary>
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        #endregion

        #region Contact Identity Fields

        /// <summary>
        /// The contact's email address.
        /// Source: <c>NextPlugin.20190204.cs</c> lines 1443–1471 (InputEmailField, Required=false).
        /// Field ID: <c>ca400904-1334-48fe-884c-223df1d08545</c>.
        /// </summary>
        [JsonPropertyName("email")]
        public string? Email { get; set; }

        /// <summary>
        /// The contact's job title or position.
        /// Source: <c>NextPlugin.20190204.cs</c> lines 1473–1501 (InputTextField, Required=false).
        /// Field ID: <c>ddcc1807-6651-411d-9eed-668ee34d0c1b</c>.
        /// </summary>
        [JsonPropertyName("job_title")]
        public string? JobTitle { get; set; }

        /// <summary>
        /// The contact's first (given) name. Required field per AAP specification.
        /// Source: <c>NextPlugin.20190204.cs</c> lines 1503–1531 (InputTextField).
        /// Field ID: <c>6670c70c-c46e-4912-a70f-b1ad20816415</c>.
        /// </summary>
        [JsonPropertyName("first_name")]
        public string FirstName { get; set; }

        /// <summary>
        /// The contact's last (family) name. Required field per AAP specification.
        /// Source: <c>NextPlugin.20190204.cs</c> lines 1533–1561 (InputTextField).
        /// Field ID: <c>4f711d55-11a7-464a-a4c3-3b3047c6c014</c>.
        /// </summary>
        [JsonPropertyName("last_name")]
        public string LastName { get; set; }

        /// <summary>
        /// Free-form notes associated with the contact.
        /// Source: <c>NextPlugin.20190204.cs</c> lines 1563–1592 (InputMultiLineTextField, Required=false).
        /// Field ID: <c>9912ff90-bc26-4879-9615-c5963a42fe22</c>.
        /// </summary>
        [JsonPropertyName("notes")]
        public string? Notes { get; set; }

        #endregion

        #region Phone Fields

        /// <summary>
        /// The contact's fixed (landline) phone number.
        /// Source: <c>NextPlugin.20190204.cs</c> lines 1594–1623 (InputPhoneField, Required=false).
        /// Field ID: <c>0f947ba0-ccac-40c4-9d31-5e5f5be953ce</c>.
        /// </summary>
        [JsonPropertyName("fixed_phone")]
        public string? FixedPhone { get; set; }

        /// <summary>
        /// The contact's mobile (cellular) phone number.
        /// Source: <c>NextPlugin.20190204.cs</c> lines 1625–1654 (InputPhoneField, Required=false).
        /// Field ID: <c>519bd797-1dc7-4aef-b1ed-f27442f855ef</c>.
        /// </summary>
        [JsonPropertyName("mobile_phone")]
        public string? MobilePhone { get; set; }

        /// <summary>
        /// The contact's fax phone number.
        /// Source: <c>NextPlugin.20190204.cs</c> lines 1656–1685 (InputPhoneField, Required=false).
        /// Field ID: <c>0475b344-8f8e-464c-a182-9c2beae105f3</c>.
        /// </summary>
        [JsonPropertyName("fax_phone")]
        public string? FaxPhone { get; set; }

        #endregion

        #region Salutation

        /// <summary>
        /// The identifier linking this contact to a salutation record (e.g., Mr., Mrs., Dr.).
        /// Required field with a default value of <see cref="DefaultSalutationId"/>.
        /// Source: <c>NextPlugin.20190206.cs</c> lines 519–547 (InputGuidField, Required=true,
        /// DefaultValue=<c>87c08ee1-8d4d-4c89-9b37-4e3cc3f98698</c>).
        /// Field ID: <c>afd8d03c-8bd8-44f8-8c46-b13e57cffa30</c>.
        /// <para>
        /// Note: Replaces the misspelled <c>solutation_id</c> field from <c>NextPlugin.20190204.cs</c>
        /// (lines 1687–1715, field ID <c>66b49907-2c0f-4914-a71c-1a9ccba1c704</c>),
        /// which was deleted in <c>NextPlugin.20190206.cs</c> (lines 45–53).
        /// </para>
        /// </summary>
        [JsonPropertyName("salutation_id")]
        public Guid SalutationId { get; set; }

        #endregion

        #region Address Fields

        /// <summary>
        /// The city portion of the contact's address.
        /// Source: <c>NextPlugin.20190204.cs</c> lines 1717–1745 (InputTextField, Required=false).
        /// Field ID: <c>acc25b72-6e17-437f-bfaf-f514b0a7406f</c>.
        /// </summary>
        [JsonPropertyName("city")]
        public string? City { get; set; }

        /// <summary>
        /// The identifier linking this contact to a country record.
        /// Source: <c>NextPlugin.20190204.cs</c> lines 1747–1775 (InputGuidField, Required=false).
        /// Field ID: <c>08a67742-21ef-4ecb-8872-54ac18b50bdc</c>.
        /// </summary>
        [JsonPropertyName("country_id")]
        public Guid? CountryId { get; set; }

        /// <summary>
        /// The region, state, or province of the contact's address.
        /// Source: <c>NextPlugin.20190204.cs</c> lines 1777–1805 (InputTextField, Required=false).
        /// Field ID: <c>f5cab626-c215-4922-be4f-8931d0cf0b66</c>.
        /// </summary>
        [JsonPropertyName("region")]
        public string? Region { get; set; }

        /// <summary>
        /// The primary street address line for the contact.
        /// Source: <c>NextPlugin.20190204.cs</c> lines 1807–1835 (InputTextField, Required=false).
        /// Field ID: <c>1147a14a-d9ae-4c88-8441-80f668676b1c</c>.
        /// </summary>
        [JsonPropertyName("street")]
        public string? Street { get; set; }

        /// <summary>
        /// The secondary street address line for the contact (suite, apartment, etc.).
        /// Source: <c>NextPlugin.20190204.cs</c> lines 1837–1865 (InputTextField, Required=false).
        /// Field ID: <c>2b1532c0-528c-4dfb-b40a-3d75ef1491fc</c>.
        /// </summary>
        [JsonPropertyName("street_2")]
        public string? Street2 { get; set; }

        /// <summary>
        /// The postal/ZIP code of the contact's address.
        /// Source: <c>NextPlugin.20190204.cs</c> lines 1867–1895 (InputTextField, Required=false).
        /// Field ID: <c>c3433c76-dee9-4dce-94a0-ea5f03527ee6</c>.
        /// </summary>
        [JsonPropertyName("post_code")]
        public string? PostCode { get; set; }

        #endregion

        #region Metadata Fields

        /// <summary>
        /// The date and time when this contact record was created. Required field that defaults
        /// to the current UTC time. Format in source: <c>yyyy-MMM-dd HH:mm</c>.
        /// Source: <c>NextPlugin.20190206.cs</c> lines 429–458 (InputDateTimeField, Required=true,
        /// UseCurrentTimeAsDefaultValue=true).
        /// Field ID: <c>52f89031-2d6d-47af-ba28-40da08b040ae</c>.
        /// </summary>
        [JsonPropertyName("created_on")]
        public DateTime CreatedOn { get; set; }

        /// <summary>
        /// URL or path to the contact's photo/avatar image.
        /// Source: <c>NextPlugin.20190206.cs</c> lines 460–487 (InputImageField, Required=false).
        /// Field ID: <c>63e82ecb-ff4e-4fd0-91be-6278875ea39c</c>.
        /// </summary>
        [JsonPropertyName("photo")]
        public string? Photo { get; set; }

        /// <summary>
        /// Composite search index field that aggregates searchable contact attributes
        /// (name, email, phone, etc.) into a single searchable text column.
        /// Used by the CRM service's <c>SearchService</c> for full-text search via
        /// DynamoDB GSI. Required field with an empty string default.
        /// Source: <c>NextPlugin.20190206.cs</c> lines 489–517 (InputTextField, Required=true,
        /// Searchable=true, Label="Search Index", PlaceholderText="Search contacts",
        /// DefaultValue="").
        /// Field ID: <c>6d33f297-1cd4-4b75-a0cf-1887b7a3ced8</c>.
        /// </summary>
        [JsonPropertyName("x_search")]
        public string? XSearch { get; set; }

        /// <summary>
        /// Scope field used for multi-tenant or context-based filtering of contact records.
        /// Included per AAP specification. Maps to the <c>l_scope</c> field pattern used
        /// across entity definitions (see <c>NextPlugin.20190206.cs</c> lines 55–83 for
        /// the <c>l_scope</c> field definition on the language entity).
        /// </summary>
        [JsonPropertyName("l_scope")]
        public string? LScope { get; set; }

        #endregion
    }
}
