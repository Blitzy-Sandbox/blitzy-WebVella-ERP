using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Tests.Crm.Fixtures
{
    /// <summary>
    /// Container class providing factory access to all CRM entity test data builders.
    /// Follows the Builder pattern to enable fluent construction of <see cref="EntityRecord"/>
    /// instances representing CRM domain entities (account, contact, case, address, salutation).
    ///
    /// Usage:
    /// <code>
    ///   var builders = new TestDataBuilders();
    ///   var account = builders.Account().WithName("Acme Corp").WithType("1").Build();
    ///   var contact = builders.Contact().WithEmail("john@acme.com").Build();
    /// </code>
    /// </summary>
    public class TestDataBuilders
    {
        /// <summary>
        /// Creates a new <see cref="AccountBuilder"/> for constructing account entity records.
        /// Account entity ID: 2e22b50f-e444-4b62-a171-076e51246939
        /// </summary>
        public AccountBuilder Account() => new AccountBuilder();

        /// <summary>
        /// Creates a new <see cref="ContactBuilder"/> for constructing contact entity records.
        /// Contact entity ID: 39e1dd9b-827f-464d-95ea-507ade81cbd0
        /// </summary>
        public ContactBuilder Contact() => new ContactBuilder();

        /// <summary>
        /// Creates a new <see cref="CaseBuilder"/> for constructing case entity records.
        /// Case entity ID: 0ebb3981-7443-45c8-ab38-db0709daf58c
        /// </summary>
        public CaseBuilder Case() => new CaseBuilder();

        /// <summary>
        /// Creates a new <see cref="AddressBuilder"/> for constructing address entity records.
        /// Address entity ID: 34a126ba-1dee-4099-a1c1-a24e70eb10f0
        /// </summary>
        public AddressBuilder Address() => new AddressBuilder();

        /// <summary>
        /// Creates a new <see cref="SalutationBuilder"/> for constructing salutation entity records.
        /// Salutation entity ID: 690dc799-e732-4d17-80d8-0f761bc33def
        /// </summary>
        public SalutationBuilder Salutation() => new SalutationBuilder();
    }

    /// <summary>
    /// Fluent builder for constructing account entity records matching the schema defined in
    /// NextPlugin.20190203.cs (entity creation, name, l_scope fields) and
    /// NextPlugin.20190204.cs (type, website, street, region, post_code, fixed_phone,
    /// mobile_phone, fax_phone, notes, last_name, first_name, x_search, email, city,
    /// country_id, tax_id, street_2, language_id, currency_id fields) and
    /// NextPlugin.20190206.cs (created_on, salutation_id — corrected from solutation_id).
    ///
    /// Account entity ID: 2e22b50f-e444-4b62-a171-076e51246939
    /// Select field "type": "1" = Company, "2" = Person (default: "1")
    /// </summary>
    public class AccountBuilder
    {
        private Guid _id = Guid.NewGuid();
        private string _name = "Test Account";
        private string _type = "1";
        private string _website = "https://test.example.com";
        private string _street = "123 Test St";
        private string _street2 = "";
        private string _region = "Test Region";
        private string _postCode = "12345";
        private string _city = "Test City";
        private string _fixedPhone = "+1-555-0100";
        private string _mobilePhone = "+1-555-0101";
        private string _faxPhone = "";
        private string _email = "test@example.com";
        private string _notes = "";
        private string _firstName = "Test";
        private string _lastName = "Account";
        private string _xSearch = "";
        private Guid? _countryId = (Guid?)null;
        private string _taxId = "";
        private Guid? _salutationId = (Guid?)null;
        private Guid? _languageId = (Guid?)null;
        private Guid? _currencyId = (Guid?)null;
        private DateTime _createdOn = DateTime.UtcNow;

        /// <summary>Sets the record ID (primary key). Default: Guid.NewGuid().</summary>
        public AccountBuilder WithId(Guid id) { _id = id; return this; }

        /// <summary>Sets the account name. Required field. Default: "Test Account".</summary>
        public AccountBuilder WithName(string name) { _name = name; return this; }

        /// <summary>Sets the account type. Select field: "1" = Company, "2" = Person. Default: "1".</summary>
        public AccountBuilder WithType(string type) { _type = type; return this; }

        /// <summary>Sets the website URL. Default: "https://test.example.com".</summary>
        public AccountBuilder WithWebsite(string website) { _website = website; return this; }

        /// <summary>Sets the street address. Default: "123 Test St".</summary>
        public AccountBuilder WithStreet(string street) { _street = street; return this; }

        /// <summary>Sets the secondary street address. Default: "".</summary>
        public AccountBuilder WithStreet2(string street2) { _street2 = street2; return this; }

        /// <summary>Sets the region. Default: "Test Region".</summary>
        public AccountBuilder WithRegion(string region) { _region = region; return this; }

        /// <summary>Sets the postal code. Default: "12345".</summary>
        public AccountBuilder WithPostCode(string postCode) { _postCode = postCode; return this; }

        /// <summary>Sets the city. Default: "Test City".</summary>
        public AccountBuilder WithCity(string city) { _city = city; return this; }

        /// <summary>Sets the fixed phone number. Default: "+1-555-0100".</summary>
        public AccountBuilder WithFixedPhone(string phone) { _fixedPhone = phone; return this; }

        /// <summary>Sets the mobile phone number. Default: "+1-555-0101".</summary>
        public AccountBuilder WithMobilePhone(string phone) { _mobilePhone = phone; return this; }

        /// <summary>Sets the fax phone number. Default: "".</summary>
        public AccountBuilder WithFaxPhone(string phone) { _faxPhone = phone; return this; }

        /// <summary>Sets the email address. Default: "test@example.com".</summary>
        public AccountBuilder WithEmail(string email) { _email = email; return this; }

        /// <summary>Sets the notes multiline text. Default: "".</summary>
        public AccountBuilder WithNotes(string notes) { _notes = notes; return this; }

        /// <summary>Sets the first name. Default: "Test".</summary>
        public AccountBuilder WithFirstName(string firstName) { _firstName = firstName; return this; }

        /// <summary>Sets the last name. Default: "Account".</summary>
        public AccountBuilder WithLastName(string lastName) { _lastName = lastName; return this; }

        /// <summary>Sets the search index text. Default: "".</summary>
        public AccountBuilder WithXSearch(string xSearch) { _xSearch = xSearch; return this; }

        /// <summary>Sets the country FK (Guid). Default: null.</summary>
        public AccountBuilder WithCountryId(Guid? countryId) { _countryId = countryId; return this; }

        /// <summary>Sets the tax ID text. Default: "".</summary>
        public AccountBuilder WithTaxId(string taxId) { _taxId = taxId; return this; }

        /// <summary>Sets the salutation FK (Guid). Default: null. Corrected from misspelled "solutation_id" in patch 20190204.</summary>
        public AccountBuilder WithSalutationId(Guid? salutationId) { _salutationId = salutationId; return this; }

        /// <summary>Sets the language FK (Guid). Default: null.</summary>
        public AccountBuilder WithLanguageId(Guid? languageId) { _languageId = languageId; return this; }

        /// <summary>Sets the currency FK (Guid). Default: null.</summary>
        public AccountBuilder WithCurrencyId(Guid? currencyId) { _currencyId = currencyId; return this; }

        /// <summary>Sets the created-on timestamp. Default: DateTime.UtcNow.</summary>
        public AccountBuilder WithCreatedOn(DateTime createdOn) { _createdOn = createdOn; return this; }

        /// <summary>
        /// Constructs an <see cref="EntityRecord"/> with all configured account fields.
        /// EntityRecord is a dynamic dictionary (inherits from Expando). Fields are set
        /// as key-value pairs using the exact field names from the NextPlugin patches.
        /// </summary>
        public EntityRecord Build()
        {
            var record = new EntityRecord();
            record["id"] = _id;
            record["name"] = _name;
            record["type"] = _type;
            record["website"] = _website;
            record["street"] = _street;
            record["street_2"] = _street2;
            record["region"] = _region;
            record["post_code"] = _postCode;
            record["city"] = _city;
            record["fixed_phone"] = _fixedPhone;
            record["mobile_phone"] = _mobilePhone;
            record["fax_phone"] = _faxPhone;
            record["email"] = _email;
            record["notes"] = _notes;
            record["first_name"] = _firstName;
            record["last_name"] = _lastName;
            record["x_search"] = _xSearch;
            record["country_id"] = _countryId;
            record["tax_id"] = _taxId;
            record["salutation_id"] = _salutationId;
            record["language_id"] = _languageId;
            record["currency_id"] = _currencyId;
            record["created_on"] = _createdOn;
            return record;
        }
    }

    /// <summary>
    /// Fluent builder for constructing contact entity records matching the schema defined in
    /// NextPlugin.20190204.cs (entity creation line 1401, fields from lines 1443-1867) and
    /// NextPlugin.20190206.cs (salutation_id — corrected from solutation_id).
    ///
    /// Contact entity ID: 39e1dd9b-827f-464d-95ea-507ade81cbd0
    /// </summary>
    public class ContactBuilder
    {
        private Guid _id = Guid.NewGuid();
        private string _email = "contact@example.com";
        private string _jobTitle = "Software Engineer";
        private string _firstName = "Test";
        private string _lastName = "Contact";
        private string _notes = "";
        private string _fixedPhone = "";
        private string _mobilePhone = "";
        private string _faxPhone = "";
        private Guid? _salutationId = (Guid?)null;
        private string _city = "";
        private Guid? _countryId = (Guid?)null;
        private string _region = "";
        private string _street = "";
        private string _street2 = "";
        private string _postCode = "";
        private string _xSearch = "";

        /// <summary>Sets the record ID (primary key). Default: Guid.NewGuid().</summary>
        public ContactBuilder WithId(Guid id) { _id = id; return this; }

        /// <summary>Sets the email address. Default: "contact@example.com".</summary>
        public ContactBuilder WithEmail(string email) { _email = email; return this; }

        /// <summary>Sets the job title. Default: "Software Engineer".</summary>
        public ContactBuilder WithJobTitle(string jobTitle) { _jobTitle = jobTitle; return this; }

        /// <summary>Sets the first name. Default: "Test".</summary>
        public ContactBuilder WithFirstName(string firstName) { _firstName = firstName; return this; }

        /// <summary>Sets the last name. Default: "Contact".</summary>
        public ContactBuilder WithLastName(string lastName) { _lastName = lastName; return this; }

        /// <summary>Sets the notes multiline text. Default: "".</summary>
        public ContactBuilder WithNotes(string notes) { _notes = notes; return this; }

        /// <summary>Sets the fixed phone number. Default: "".</summary>
        public ContactBuilder WithFixedPhone(string phone) { _fixedPhone = phone; return this; }

        /// <summary>Sets the mobile phone number. Default: "".</summary>
        public ContactBuilder WithMobilePhone(string phone) { _mobilePhone = phone; return this; }

        /// <summary>Sets the fax phone number. Default: "".</summary>
        public ContactBuilder WithFaxPhone(string phone) { _faxPhone = phone; return this; }

        /// <summary>Sets the salutation FK (Guid). Default: null. Corrected from "solutation_id".</summary>
        public ContactBuilder WithSalutationId(Guid? salutationId) { _salutationId = salutationId; return this; }

        /// <summary>Sets the city. Default: "".</summary>
        public ContactBuilder WithCity(string city) { _city = city; return this; }

        /// <summary>Sets the country FK (Guid). Default: null.</summary>
        public ContactBuilder WithCountryId(Guid? countryId) { _countryId = countryId; return this; }

        /// <summary>Sets the region. Default: "".</summary>
        public ContactBuilder WithRegion(string region) { _region = region; return this; }

        /// <summary>Sets the street address. Default: "".</summary>
        public ContactBuilder WithStreet(string street) { _street = street; return this; }

        /// <summary>Sets the secondary street address. Default: "".</summary>
        public ContactBuilder WithStreet2(string street2) { _street2 = street2; return this; }

        /// <summary>Sets the postal code. Default: "".</summary>
        public ContactBuilder WithPostCode(string postCode) { _postCode = postCode; return this; }

        /// <summary>Sets the search index text. Default: "".</summary>
        public ContactBuilder WithXSearch(string xSearch) { _xSearch = xSearch; return this; }

        /// <summary>
        /// Constructs an <see cref="EntityRecord"/> with all configured contact fields.
        /// Field names match the exact definitions from NextPlugin.20190204.cs and 20190206.cs.
        /// </summary>
        public EntityRecord Build()
        {
            var record = new EntityRecord();
            record["id"] = _id;
            record["email"] = _email;
            record["job_title"] = _jobTitle;
            record["first_name"] = _firstName;
            record["last_name"] = _lastName;
            record["notes"] = _notes;
            record["fixed_phone"] = _fixedPhone;
            record["mobile_phone"] = _mobilePhone;
            record["fax_phone"] = _faxPhone;
            record["salutation_id"] = _salutationId;
            record["city"] = _city;
            record["country_id"] = _countryId;
            record["region"] = _region;
            record["street"] = _street;
            record["street_2"] = _street2;
            record["post_code"] = _postCode;
            record["x_search"] = _xSearch;
            return record;
        }
    }

    /// <summary>
    /// Fluent builder for constructing case entity records matching the schema defined in
    /// NextPlugin.20190203.cs (entity creation line 1385, fields from lines 1423-1760).
    ///
    /// Case entity ID: 0ebb3981-7443-45c8-ab38-db0709daf58c
    /// Select field "priority": "low", "medium", "high" (default: "medium")
    /// AutoNumber field "number": decimal type with starting value 1.0
    /// </summary>
    public class CaseBuilder
    {
        private Guid _id = Guid.NewGuid();
        private Guid? _accountId = (Guid?)null;
        private DateTime _createdOn = DateTime.UtcNow;
        private Guid? _createdBy = (Guid?)null;
        private Guid? _ownerId = (Guid?)null;
        private string _description = "Test case description";
        private string _subject = "Test Case Subject";
        private decimal _number = 0m;
        private DateTime? _closedOn = (DateTime?)null;
        private string _lScope = "";
        private string _priority = "medium";
        private Guid? _statusId = (Guid?)null;
        private Guid? _typeId = (Guid?)null;
        private string _xSearch = "";

        /// <summary>Sets the record ID (primary key). Default: Guid.NewGuid().</summary>
        public CaseBuilder WithId(Guid id) { _id = id; return this; }

        /// <summary>Sets the account FK (Guid). Default: null.</summary>
        public CaseBuilder WithAccountId(Guid? accountId) { _accountId = accountId; return this; }

        /// <summary>Sets the created-on timestamp. Default: DateTime.UtcNow.</summary>
        public CaseBuilder WithCreatedOn(DateTime createdOn) { _createdOn = createdOn; return this; }

        /// <summary>Sets the created-by user FK (Guid). Default: null.</summary>
        public CaseBuilder WithCreatedBy(Guid? createdBy) { _createdBy = createdBy; return this; }

        /// <summary>Sets the owner user FK (Guid). Default: null.</summary>
        public CaseBuilder WithOwnerId(Guid? ownerId) { _ownerId = ownerId; return this; }

        /// <summary>Sets the case description (HTML field). Default: "Test case description".</summary>
        public CaseBuilder WithDescription(string description) { _description = description; return this; }

        /// <summary>Sets the case subject. Default: "Test Case Subject".</summary>
        public CaseBuilder WithSubject(string subject) { _subject = subject; return this; }

        /// <summary>Sets the auto-number value. Default: 0m.</summary>
        public CaseBuilder WithNumber(decimal number) { _number = number; return this; }

        /// <summary>Sets the closed-on timestamp. Default: null (open case).</summary>
        public CaseBuilder WithClosedOn(DateTime? closedOn) { _closedOn = closedOn; return this; }

        /// <summary>Sets the scope text. Default: "".</summary>
        public CaseBuilder WithLScope(string lScope) { _lScope = lScope; return this; }

        /// <summary>Sets the priority. Select field: "low", "medium", "high". Default: "medium".</summary>
        public CaseBuilder WithPriority(string priority) { _priority = priority; return this; }

        /// <summary>Sets the status FK (Guid). Default: null.</summary>
        public CaseBuilder WithStatusId(Guid? statusId) { _statusId = statusId; return this; }

        /// <summary>Sets the type FK (Guid). Default: null.</summary>
        public CaseBuilder WithTypeId(Guid? typeId) { _typeId = typeId; return this; }

        /// <summary>Sets the search index text. Default: "".</summary>
        public CaseBuilder WithXSearch(string xSearch) { _xSearch = xSearch; return this; }

        /// <summary>
        /// Constructs an <see cref="EntityRecord"/> with all configured case fields.
        /// Field names match the exact definitions from NextPlugin.20190203.cs.
        /// </summary>
        public EntityRecord Build()
        {
            var record = new EntityRecord();
            record["id"] = _id;
            record["account_id"] = _accountId;
            record["created_on"] = _createdOn;
            record["created_by"] = _createdBy;
            record["owner_id"] = _ownerId;
            record["description"] = _description;
            record["subject"] = _subject;
            record["number"] = _number;
            record["closed_on"] = _closedOn;
            record["l_scope"] = _lScope;
            record["priority"] = _priority;
            record["status_id"] = _statusId;
            record["type_id"] = _typeId;
            record["x_search"] = _xSearch;
            return record;
        }
    }

    /// <summary>
    /// Fluent builder for constructing address entity records matching the schema defined in
    /// NextPlugin.20190204.cs (entity creation line 1897, fields from lines 1939-2148).
    ///
    /// Address entity ID: 34a126ba-1dee-4099-a1c1-a24e70eb10f0
    /// </summary>
    public class AddressBuilder
    {
        private Guid _id = Guid.NewGuid();
        private string _name = "Test Address";
        private string _street = "456 Test Ave";
        private string _street2 = "";
        private string _city = "Test City";
        private string _region = "";
        private Guid? _countryId = (Guid?)null;
        private string _notes = "";

        /// <summary>Sets the record ID (primary key). Default: Guid.NewGuid().</summary>
        public AddressBuilder WithId(Guid id) { _id = id; return this; }

        /// <summary>Sets the address name. Default: "Test Address".</summary>
        public AddressBuilder WithName(string name) { _name = name; return this; }

        /// <summary>Sets the street address. Default: "456 Test Ave".</summary>
        public AddressBuilder WithStreet(string street) { _street = street; return this; }

        /// <summary>Sets the secondary street address. Default: "".</summary>
        public AddressBuilder WithStreet2(string street2) { _street2 = street2; return this; }

        /// <summary>Sets the city. Default: "Test City".</summary>
        public AddressBuilder WithCity(string city) { _city = city; return this; }

        /// <summary>Sets the region. Default: "".</summary>
        public AddressBuilder WithRegion(string region) { _region = region; return this; }

        /// <summary>Sets the country FK (Guid). Default: null.</summary>
        public AddressBuilder WithCountryId(Guid? countryId) { _countryId = countryId; return this; }

        /// <summary>Sets the notes multiline text. Default: "".</summary>
        public AddressBuilder WithNotes(string notes) { _notes = notes; return this; }

        /// <summary>
        /// Constructs an <see cref="EntityRecord"/> with all configured address fields.
        /// Field names match the exact definitions from NextPlugin.20190204.cs.
        /// </summary>
        public EntityRecord Build()
        {
            var record = new EntityRecord();
            record["id"] = _id;
            record["name"] = _name;
            record["street"] = _street;
            record["street_2"] = _street2;
            record["city"] = _city;
            record["region"] = _region;
            record["country_id"] = _countryId;
            record["notes"] = _notes;
            return record;
        }
    }

    /// <summary>
    /// Fluent builder for constructing salutation entity records matching the schema defined in
    /// NextPlugin.20190206.cs (entity creation line 613, fields from lines 651-828).
    ///
    /// Salutation entity ID: 690dc799-e732-4d17-80d8-0f761bc33def
    /// </summary>
    public class SalutationBuilder
    {
        private Guid _id = Guid.NewGuid();
        private string _label = "Mr.";
        private bool _isDefault = false;
        private bool _isEnabled = true;
        private bool _isSystem = false;
        private decimal _sortIndex = 0m;
        private string _lScope = "";

        /// <summary>Sets the record ID (primary key). Default: Guid.NewGuid().</summary>
        public SalutationBuilder WithId(Guid id) { _id = id; return this; }

        /// <summary>Sets the salutation label text. Default: "Mr.".</summary>
        public SalutationBuilder WithLabel(string label) { _label = label; return this; }

        /// <summary>Sets whether this is the default salutation. Default: false.</summary>
        public SalutationBuilder WithIsDefault(bool isDefault) { _isDefault = isDefault; return this; }

        /// <summary>Sets whether this salutation is enabled. Default: true.</summary>
        public SalutationBuilder WithIsEnabled(bool isEnabled) { _isEnabled = isEnabled; return this; }

        /// <summary>Sets whether this is a system salutation. Default: false.</summary>
        public SalutationBuilder WithIsSystem(bool isSystem) { _isSystem = isSystem; return this; }

        /// <summary>Sets the sort index for display ordering. Default: 0m.</summary>
        public SalutationBuilder WithSortIndex(decimal sortIndex) { _sortIndex = sortIndex; return this; }

        /// <summary>Sets the scope text. Default: "".</summary>
        public SalutationBuilder WithLScope(string lScope) { _lScope = lScope; return this; }

        /// <summary>
        /// Constructs an <see cref="EntityRecord"/> with all configured salutation fields.
        /// Field names match the exact definitions from NextPlugin.20190206.cs.
        /// </summary>
        public EntityRecord Build()
        {
            var record = new EntityRecord();
            record["id"] = _id;
            record["label"] = _label;
            record["is_default"] = _isDefault;
            record["is_enabled"] = _isEnabled;
            record["is_system"] = _isSystem;
            record["sort_index"] = _sortIndex;
            record["l_scope"] = _lScope;
            return record;
        }
    }

    /// <summary>
    /// Static helper class providing pre-configured entity builders for common test scenarios.
    /// Each method returns a builder pre-populated with valid data representing a specific entity
    /// configuration, ready to be customized further or built immediately.
    ///
    /// Usage:
    /// <code>
    ///   // Use directly
    ///   var account = TestDataDefaults.ValidCompanyAccount().Build();
    ///   // Customize before building
    ///   var contact = TestDataDefaults.ValidContact().WithEmail("custom@test.com").Build();
    /// </code>
    /// </summary>
    public static class TestDataDefaults
    {
        /// <summary>
        /// Returns an <see cref="AccountBuilder"/> pre-configured as a valid company account
        /// with type "1" (Company), realistic business name, address, contact info, and all
        /// required fields populated to pass CRM service validation.
        /// </summary>
        public static AccountBuilder ValidCompanyAccount()
        {
            return new AccountBuilder()
                .WithName("Acme Corporation")
                .WithType("1")
                .WithWebsite("https://acme.example.com")
                .WithStreet("100 Main Street")
                .WithCity("Springfield")
                .WithRegion("Illinois")
                .WithPostCode("62701")
                .WithFixedPhone("+1-555-0100")
                .WithEmail("info@acme.example.com")
                .WithFirstName("Acme")
                .WithLastName("Corporation")
                .WithTaxId("US-123456789");
        }

        /// <summary>
        /// Returns an <see cref="AccountBuilder"/> pre-configured as a valid person account
        /// with type "2" (Person), realistic personal name and contact info, and all
        /// required fields populated to pass CRM service validation.
        /// </summary>
        public static AccountBuilder ValidPersonAccount()
        {
            return new AccountBuilder()
                .WithName("Jane Smith")
                .WithType("2")
                .WithStreet("42 Oak Avenue")
                .WithCity("Portland")
                .WithRegion("Oregon")
                .WithPostCode("97201")
                .WithMobilePhone("+1-555-0200")
                .WithEmail("jane.smith@example.com")
                .WithFirstName("Jane")
                .WithLastName("Smith");
        }

        /// <summary>
        /// Returns a <see cref="ContactBuilder"/> pre-configured with valid contact data
        /// including name, email, job title, and address information to pass CRM service validation.
        /// </summary>
        public static ContactBuilder ValidContact()
        {
            return new ContactBuilder()
                .WithFirstName("John")
                .WithLastName("Doe")
                .WithEmail("john.doe@example.com")
                .WithJobTitle("Senior Developer")
                .WithFixedPhone("+1-555-0300")
                .WithMobilePhone("+1-555-0301")
                .WithStreet("200 Technology Drive")
                .WithCity("San Francisco")
                .WithRegion("California")
                .WithPostCode("94105");
        }

        /// <summary>
        /// Returns a <see cref="CaseBuilder"/> pre-configured with valid case data
        /// including subject, description, priority, and timestamps to pass CRM service validation.
        /// </summary>
        public static CaseBuilder ValidCase()
        {
            return new CaseBuilder()
                .WithSubject("Product Inquiry")
                .WithDescription("Customer requested information about product features and pricing.")
                .WithPriority("medium")
                .WithNumber(1m)
                .WithCreatedBy(Guid.NewGuid())
                .WithOwnerId(Guid.NewGuid())
                .WithAccountId(Guid.NewGuid());
        }

        /// <summary>
        /// Returns an <see cref="AddressBuilder"/> pre-configured with valid address data
        /// including street, city, and name to pass CRM service validation.
        /// </summary>
        public static AddressBuilder ValidAddress()
        {
            return new AddressBuilder()
                .WithName("Headquarters")
                .WithStreet("500 Corporate Blvd")
                .WithCity("Austin")
                .WithRegion("Texas");
        }

        /// <summary>
        /// Returns a <see cref="SalutationBuilder"/> pre-configured with valid salutation data
        /// representing an enabled, non-system salutation to pass CRM service validation.
        /// </summary>
        public static SalutationBuilder ValidSalutation()
        {
            return new SalutationBuilder()
                .WithLabel("Mr.")
                .WithIsDefault(false)
                .WithIsEnabled(true)
                .WithIsSystem(false)
                .WithSortIndex(1m);
        }
    }
}
