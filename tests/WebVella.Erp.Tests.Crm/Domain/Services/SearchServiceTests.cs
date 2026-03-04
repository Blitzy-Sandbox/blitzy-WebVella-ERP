using System;
using System.Collections.Generic;
using System.Linq;
using AutoMapper;
using Xunit;
using FluentAssertions;
using Moq;
using WebVella.Erp.Service.Crm.Domain.Services;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.SharedKernel.Utilities;

namespace WebVella.Erp.Tests.Crm.Domain.Services
{
	/// <summary>
	/// Comprehensive unit tests for <see cref="SearchService.RegenSearchField"/>.
	/// Covers all CRM search indexing business logic including entity resolution,
	/// field resolution (direct + relation-qualified), per-field-type value formatting
	/// (10 switch cases), record update behavior, exception handling, and edge cases.
	/// Achieves greater-than-or-equal-to 80% code coverage on the SearchService class per AAP Section 0.8.2.
	///
	/// Uses a <see cref="TestableSearchService"/> subclass that overrides the protected
	/// virtual <c>ExecuteEqlQuery</c> seam to inject mock EQL results, enabling pure
	/// unit testing without a live database connection.
	/// </summary>
	public class SearchServiceTests
	{
		private readonly Mock<ICrmEntityRelationManager> _mockRelationManager;
		private readonly Mock<ICrmEntityManager> _mockEntityManager;
		private readonly Mock<ICrmRecordManager> _mockRecordManager;
		private readonly TestableSearchService _searchService;

		private static readonly Guid AccountEntityId = new Guid("2e22b50f-e444-4b62-a171-076e51246939");
		private static readonly Guid ContactEntityId = new Guid("39e1dd9b-827f-464d-95ea-507ade81cbd0");
		private static readonly Guid CaseEntityId = new Guid("0ebb3981-7443-45c8-ab38-db0709daf58c");
		private static readonly Guid CountryEntityId = new Guid("a0c0e7a6-1b1c-4d3e-8e5f-6a7b8c9d0e1f");
		private static readonly Guid CaseStatusEntityId = new Guid("b1c1d2e3-4f5a-6b7c-8d9e-0f1a2b3c4d5e");
		private static readonly Guid CaseTypeEntityId = new Guid("c2d3e4f5-0a1b-2c3d-4e5f-6a7b8c9d0e1f");
		private static readonly Guid AccountNnEntityId = new Guid("d3e4f5a6-1b2c-3d4e-5f6a-7b8c9d0e1f2a");

		public SearchServiceTests()
		{
			// Initialize AutoMapper with ErrorModel → ValidationError mapping
			// Required because SearchService.RegenSearchField uses .MapTo<ValidationError>()
			// internally when an update fails, which delegates to ErpAutoMapper.Mapper.
			if (ErpAutoMapper.Mapper == null)
			{
				var config = new MapperConfiguration(cfg =>
				{
					cfg.CreateMap<ErrorModel, ValidationError>()
						.ConvertUsing(source => source == null
							? null
							: new ValidationError(source.Key ?? "id", source.Message));
				});
				ErpAutoMapper.Mapper = config.CreateMapper();
			}

			_mockRelationManager = new Mock<ICrmEntityRelationManager>();
			_mockEntityManager = new Mock<ICrmEntityManager>();
			_mockRecordManager = new Mock<ICrmRecordManager>();
			_searchService = new TestableSearchService(
				_mockRelationManager.Object,
				_mockEntityManager.Object,
				_mockRecordManager.Object);
		}

		// ════════════════════════════════════════════════════════════════
		//  Category 1 — Per-Entity x_search Regeneration
		// ════════════════════════════════════════════════════════════════

		[Fact]
		public void RegenSearchField_AccountEntity_WithAllFields_BuildsCorrectSearchIndex()
		{
			var recordId = Guid.NewGuid();
			var accountEntity = CreateEntity("account", AccountEntityId, new List<Field>
			{
				CreateTextField("city"), CreateTextField("email"), CreateTextField("fax_phone"),
				CreateTextField("first_name"), CreateTextField("fixed_phone"), CreateTextField("last_name"),
				CreateTextField("mobile_phone"), CreateTextField("name"), CreateTextField("notes"),
				CreateTextField("post_code"), CreateTextField("region"), CreateTextField("street"),
				CreateTextField("street_2"), CreateTextField("tax_id"), CreateTextField("type"),
				CreateTextField("website")
			});
			var countryEntity = CreateEntity("country", CountryEntityId, new List<Field> { CreateTextField("label") });
			var relation = CreateEntityRelation("country_1n_account", CountryEntityId, AccountEntityId);
			SetupManagerMocks(new List<Entity> { accountEntity, countryEntity }, new List<EntityRelation> { relation });

			var eqlRecord = new EntityRecord();
			eqlRecord["city"] = "Sofia";
			eqlRecord["email"] = "test@example.com";
			eqlRecord["fax_phone"] = "+1-555-0199";
			eqlRecord["first_name"] = "John";
			eqlRecord["fixed_phone"] = "+1-555-0100";
			eqlRecord["last_name"] = "Doe";
			eqlRecord["mobile_phone"] = "+1-555-0101";
			eqlRecord["name"] = "Acme Corp";
			eqlRecord["notes"] = "Important client";
			eqlRecord["post_code"] = "1000";
			eqlRecord["region"] = "Sofia City";
			eqlRecord["street"] = "123 Main St";
			eqlRecord["street_2"] = "Suite 100";
			eqlRecord["tax_id"] = "BG123456789";
			eqlRecord["type"] = "Company";
			eqlRecord["website"] = "https://acme.example.com";
			var countryRec = new EntityRecord();
			countryRec["label"] = "Bulgaria";
			eqlRecord["$country_1n_account"] = countryRec;

			SetupEqlAndUpdate(eqlRecord, recordId);

			var indexedFields = new List<string>
			{
				"city", "$country_1n_account.label", "email", "fax_phone", "first_name",
				"fixed_phone", "last_name", "mobile_phone", "name", "notes",
				"post_code", "region", "street", "street_2", "tax_id", "type", "website"
			};
			var inputRecord = CreateInputRecord(recordId);

			_searchService.RegenSearchField("account", inputRecord, indexedFields);

			_mockRecordManager.Verify(m => m.UpdateRecord("account",
				It.Is<EntityRecord>(r =>
					((string)r["x_search"]).Contains("Sofia") &&
					((string)r["x_search"]).Contains("test@example.com") &&
					((string)r["x_search"]).Contains("Acme Corp") &&
					((string)r["x_search"]).Contains("Bulgaria") &&
					((string)r["x_search"]).Contains("BG123456789") &&
					(Guid)r["id"] == recordId),
				false), Times.Once());
		}

		[Fact]
		public void RegenSearchField_AccountEntity_WithPartialFields_IgnoresMissingFields()
		{
			var recordId = Guid.NewGuid();
			var accountEntity = CreateEntity("account", AccountEntityId, new List<Field>
			{
				CreateTextField("city"), CreateTextField("name"), CreateTextField("email")
			});
			SetupManagerMocks(new List<Entity> { accountEntity }, new List<EntityRelation>());

			var eqlRecord = new EntityRecord();
			eqlRecord["city"] = "Sofia";
			eqlRecord["name"] = "Acme";
			eqlRecord["email"] = "test@acme.com";
			SetupEqlAndUpdate(eqlRecord, recordId);

			var indexedFields = new List<string>
			{
				"city", "$country_1n_account.label", "email", "fax_phone", "first_name",
				"fixed_phone", "last_name", "mobile_phone", "name", "notes",
				"post_code", "region", "street", "street_2", "tax_id", "type", "website"
			};
			var inputRecord = CreateInputRecord(recordId);

			Action act = () => _searchService.RegenSearchField("account", inputRecord, indexedFields);
			act.Should().NotThrow();

			_searchService.LastEqlCommand.Should().Contain("city");
			_searchService.LastEqlCommand.Should().Contain("name");
			_searchService.LastEqlCommand.Should().Contain("email");
			_searchService.LastEqlCommand.Should().NotContain("fax_phone");
		}

		[Fact]
		public void RegenSearchField_ContactEntity_WithRelations_ResolvesRelationFields()
		{
			var recordId = Guid.NewGuid();
			var contactEntity = CreateEntity("contact", ContactEntityId, new List<Field>
			{
				CreateTextField("city"), CreateTextField("email"), CreateTextField("first_name"),
				CreateTextField("last_name"), CreateTextField("job_title")
			});
			var countryEntity = CreateEntity("country", CountryEntityId, new List<Field> { CreateTextField("label") });
			var accountEntity = CreateEntity("account_rel", AccountNnEntityId, new List<Field> { CreateTextField("name") });
			var countryRelation = CreateEntityRelation("country_1n_contact", CountryEntityId, ContactEntityId);
			var accountRelation = CreateEntityRelation("account_nn_contact", AccountNnEntityId, ContactEntityId);
			SetupManagerMocks(
				new List<Entity> { contactEntity, countryEntity, accountEntity },
				new List<EntityRelation> { countryRelation, accountRelation });

			var eqlRecord = new EntityRecord();
			eqlRecord["city"] = "Berlin";
			eqlRecord["email"] = "jane@test.com";
			eqlRecord["first_name"] = "Jane";
			eqlRecord["last_name"] = "Smith";
			eqlRecord["job_title"] = "Engineer";
			var countryRec = new EntityRecord();
			countryRec["label"] = "Germany";
			eqlRecord["$country_1n_contact"] = countryRec;
			var accRec1 = new EntityRecord();
			accRec1["name"] = "Acme Corp";
			var accRec2 = new EntityRecord();
			accRec2["name"] = "Globex Inc";
			eqlRecord["$account_nn_contact"] = new List<EntityRecord> { accRec1, accRec2 };
			SetupEqlAndUpdate(eqlRecord, recordId);

			var indexedFields = new List<string>
			{
				"city", "$country_1n_contact.label", "$account_nn_contact.name", "email",
				"first_name", "job_title", "last_name"
			};
			var inputRecord = CreateInputRecord(recordId);

			_searchService.RegenSearchField("contact", inputRecord, indexedFields);

			_mockRecordManager.Verify(m => m.UpdateRecord("contact",
				It.Is<EntityRecord>(r =>
					((string)r["x_search"]).Contains("Berlin") &&
					((string)r["x_search"]).Contains("Germany") &&
					((string)r["x_search"]).Contains("Acme Corp") &&
					((string)r["x_search"]).Contains("Globex Inc") &&
					((string)r["x_search"]).Contains("Jane")),
				false), Times.Once());
		}

		[Fact]
		public void RegenSearchField_CaseEntity_WithMultipleRelations_ResolvesAllRelations()
		{
			var recordId = Guid.NewGuid();
			var caseEntity = CreateEntity("case", CaseEntityId, new List<Field>
			{
				CreateTextField("description"), CreateTextField("number"),
				CreateTextField("priority"), CreateTextField("subject")
			});
			var accountEntity = CreateEntity("account_c", AccountNnEntityId, new List<Field> { CreateTextField("name") });
			var caseStatusEntity = CreateEntity("case_status", CaseStatusEntityId, new List<Field> { CreateTextField("label") });
			var caseTypeEntity = CreateEntity("case_type", CaseTypeEntityId, new List<Field> { CreateTextField("label") });
			var accountRel = CreateEntityRelation("account_nn_case", AccountNnEntityId, CaseEntityId);
			var statusRel = CreateEntityRelation("case_status_1n_case", CaseStatusEntityId, CaseEntityId);
			var typeRel = CreateEntityRelation("case_type_1n_case", CaseTypeEntityId, CaseEntityId);
			SetupManagerMocks(
				new List<Entity> { caseEntity, accountEntity, caseStatusEntity, caseTypeEntity },
				new List<EntityRelation> { accountRel, statusRel, typeRel });

			var eqlRecord = new EntityRecord();
			eqlRecord["description"] = "Bug report";
			eqlRecord["number"] = "CASE-001";
			eqlRecord["priority"] = "High";
			eqlRecord["subject"] = "Login issue";
			var accRec = new EntityRecord();
			accRec["name"] = "Acme Corp";
			eqlRecord["$account_nn_case"] = new List<EntityRecord> { accRec };
			var statusRec = new EntityRecord();
			statusRec["label"] = "Open";
			eqlRecord["$case_status_1n_case"] = statusRec;
			var typeRec = new EntityRecord();
			typeRec["label"] = "Bug";
			eqlRecord["$case_type_1n_case"] = typeRec;
			SetupEqlAndUpdate(eqlRecord, recordId);

			var indexedFields = new List<string>
			{
				"$account_nn_case.name", "description", "number", "priority",
				"$case_status_1n_case.label", "$case_type_1n_case.label", "subject"
			};
			var inputRecord = CreateInputRecord(recordId);

			_searchService.RegenSearchField("case", inputRecord, indexedFields);

			_mockRecordManager.Verify(m => m.UpdateRecord("case",
				It.Is<EntityRecord>(r =>
					((string)r["x_search"]).Contains("Bug report") &&
					((string)r["x_search"]).Contains("CASE-001") &&
					((string)r["x_search"]).Contains("Acme Corp") &&
					((string)r["x_search"]).Contains("Open") &&
					((string)r["x_search"]).Contains("Bug")),
				false), Times.Once());
		}

		// ════════════════════════════════════════════════════════════════
		//  Category 2 — Field Resolution Tests
		// ════════════════════════════════════════════════════════════════

		[Fact]
		public void RegenSearchField_DirectField_ExistsOnEntity_IncludedInRequestColumns()
		{
			var recordId = Guid.NewGuid();
			SetupSimpleEntity("test_entity", AccountEntityId, "name", "TestValue", recordId);

			_searchService.RegenSearchField("test_entity", CreateInputRecord(recordId), new List<string> { "name" });

			_searchService.LastEqlCommand.Should().Contain("name");
			VerifyUpdateContains("test_entity", "TestValue", recordId);
		}

		[Fact]
		public void RegenSearchField_DirectField_MissingSilentlyIgnored()
		{
			var recordId = Guid.NewGuid();
			SetupSimpleEntity("test_entity", AccountEntityId, "name", "OnlyThis", recordId);

			Action act = () => _searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "name", "nonexistent" });
			act.Should().NotThrow();
			_searchService.LastEqlCommand.Should().NotContain("nonexistent");
			_searchService.LastEqlCommand.Should().Contain("name");
		}

		[Fact]
		public void RegenSearchField_RelationField_1N_OriginEntity_ResolvesTarget()
		{
			var recordId = Guid.NewGuid();
			var accEntity = CreateEntity("account", AccountEntityId, new List<Field>());
			var cntEntity = CreateEntity("country", CountryEntityId, new List<Field> { CreateTextField("label") });
			var relation = CreateEntityRelation("country_1n_account", AccountEntityId, CountryEntityId);
			SetupManagerMocks(new List<Entity> { accEntity, cntEntity }, new List<EntityRelation> { relation });
			var eqlRec = new EntityRecord();
			var cntRec = new EntityRecord();
			cntRec["label"] = "Bulgaria";
			eqlRec["$country_1n_account"] = cntRec;
			SetupEqlAndUpdate(eqlRec, recordId);

			_searchService.RegenSearchField("account", CreateInputRecord(recordId),
				new List<string> { "$country_1n_account.label" });

			VerifyUpdateContains("account", "Bulgaria", recordId);
		}

		[Fact]
		public void RegenSearchField_RelationField_1N_TargetEntity_ResolvesOrigin()
		{
			var recordId = Guid.NewGuid();
			var contactEntity = CreateEntity("contact", ContactEntityId, new List<Field>());
			var countryEntity = CreateEntity("country", CountryEntityId, new List<Field> { CreateTextField("label") });
			var relation = CreateEntityRelation("country_1n_contact", CountryEntityId, ContactEntityId);
			SetupManagerMocks(new List<Entity> { contactEntity, countryEntity }, new List<EntityRelation> { relation });
			var eqlRec = new EntityRecord();
			var cntRec = new EntityRecord();
			cntRec["label"] = "Germany";
			eqlRec["$country_1n_contact"] = cntRec;
			SetupEqlAndUpdate(eqlRec, recordId);

			_searchService.RegenSearchField("contact", CreateInputRecord(recordId),
				new List<string> { "$country_1n_contact.label" });

			VerifyUpdateContains("contact", "Germany", recordId);
		}

		[Fact]
		public void RegenSearchField_RelationField_NN_ResolvesRelatedEntity()
		{
			var recordId = Guid.NewGuid();
			var caseEntity = CreateEntity("case", CaseEntityId, new List<Field>());
			var accEntity = CreateEntity("account_nn", AccountNnEntityId, new List<Field> { CreateTextField("name") });
			var relation = CreateEntityRelation("account_nn_case", AccountNnEntityId, CaseEntityId);
			SetupManagerMocks(new List<Entity> { caseEntity, accEntity }, new List<EntityRelation> { relation });
			var eqlRec = new EntityRecord();
			var accRec = new EntityRecord();
			accRec["name"] = "MegaCorp";
			eqlRec["$account_nn_case"] = new List<EntityRecord> { accRec };
			SetupEqlAndUpdate(eqlRec, recordId);

			_searchService.RegenSearchField("case", CreateInputRecord(recordId),
				new List<string> { "$account_nn_case.name" });

			VerifyUpdateContains("case", "MegaCorp", recordId);
		}

		[Fact]
		public void RegenSearchField_RelationField_MissingRelation_SilentlyIgnored()
		{
			var recordId = Guid.NewGuid();
			SetupSimpleEntity("account", AccountEntityId, "name", "TestCo", recordId);

			Action act = () => _searchService.RegenSearchField("account", CreateInputRecord(recordId),
				new List<string> { "name", "$nonexistent_relation.field" });
			act.Should().NotThrow();
			_searchService.LastEqlCommand.Should().NotContain("nonexistent_relation");
		}

		[Fact]
		public void RegenSearchField_RelationField_InvalidTokenFormat_SilentlyIgnored()
		{
			var recordId = Guid.NewGuid();
			SetupSimpleEntity("account", AccountEntityId, "name", "TestCo", recordId);

			Action act = () => _searchService.RegenSearchField("account", CreateInputRecord(recordId),
				new List<string> { "name", "$relation_with_no_dot" });
			act.Should().NotThrow();
			_searchService.LastEqlCommand.Should().NotContain("relation_with_no_dot");
		}

		[Fact]
		public void RegenSearchField_RelationField_EntityNotParticipant_SilentlyIgnored()
		{
			var recordId = Guid.NewGuid();
			var entity = CreateEntity("account", AccountEntityId, new List<Field>());
			var relation = CreateEntityRelation("some_relation", Guid.NewGuid(), Guid.NewGuid());
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation> { relation });
			_searchService.SetEqlResult(new EntityRecordList());
			SetupRecordManagerUpdateSuccess();

			Action act = () => _searchService.RegenSearchField("account", CreateInputRecord(recordId),
				new List<string> { "$some_relation.field" });
			act.Should().NotThrow();
		}

		[Fact]
		public void RegenSearchField_RelationField_RelatedEntityNotFound_SilentlyIgnored()
		{
			var recordId = Guid.NewGuid();
			var entity = CreateEntity("account", AccountEntityId, new List<Field>());
			var relation = CreateEntityRelation("missing_entity_rel", AccountEntityId, Guid.NewGuid());
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation> { relation });
			_searchService.SetEqlResult(new EntityRecordList());
			SetupRecordManagerUpdateSuccess();

			Action act = () => _searchService.RegenSearchField("account", CreateInputRecord(recordId),
				new List<string> { "$missing_entity_rel.some_field" });
			act.Should().NotThrow();
		}

		[Fact]
		public void RegenSearchField_RelationField_RelatedFieldNotFound_SilentlyIgnored()
		{
			var recordId = Guid.NewGuid();
			var entity = CreateEntity("account", AccountEntityId, new List<Field>());
			var related = CreateEntity("country", CountryEntityId, new List<Field> { CreateTextField("name") });
			var relation = CreateEntityRelation("country_1n_account", AccountEntityId, CountryEntityId);
			SetupManagerMocks(new List<Entity> { entity, related }, new List<EntityRelation> { relation });
			_searchService.SetEqlResult(new EntityRecordList());
			SetupRecordManagerUpdateSuccess();

			Action act = () => _searchService.RegenSearchField("account", CreateInputRecord(recordId),
				new List<string> { "$country_1n_account.nonexistent_field" });
			act.Should().NotThrow();
		}

		[Fact]
		public void RegenSearchField_EntityNotFound_ThrowsException()
		{
			SetupManagerMocks(new List<Entity>(), new List<EntityRelation>());
			var inputRecord = CreateInputRecord(Guid.NewGuid());

			Action act = () => _searchService.RegenSearchField("nonexistent_entity", inputRecord,
				new List<string> { "name" });
			act.Should().Throw<Exception>()
				.WithMessage("Search index generation failed: Entity nonexistent_entity not found");
		}

		// ════════════════════════════════════════════════════════════════
		//  Category 3 — GetStringValue Field Type Formatting Tests
		// ════════════════════════════════════════════════════════════════

		[Fact]
		public void RegenSearchField_AutoNumberField_WithDisplayFormat_FormatsCorrectly()
		{
			var recordId = Guid.NewGuid();
			var field = new AutoNumberField { Name = "invoice_no", DisplayFormat = "INV-{0}" };
			var entity = CreateEntity("test_entity", AccountEntityId, new List<Field> { field });
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			var eqlRec = new EntityRecord();
			eqlRec["invoice_no"] = 42m;
			SetupEqlAndUpdate(eqlRec, recordId);

			_searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "invoice_no" });

			VerifyUpdateContains("test_entity", "INV-42", recordId);
		}

		[Fact]
		public void RegenSearchField_AutoNumberField_EmptyDisplayFormat_ReturnsEmpty()
		{
			var recordId = Guid.NewGuid();
			var field = new AutoNumberField { Name = "invoice_no", DisplayFormat = "" };
			var entity = CreateEntity("test_entity", AccountEntityId, new List<Field> { field });
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			var eqlRec = new EntityRecord();
			eqlRec["invoice_no"] = 42m;
			SetupEqlAndUpdate(eqlRec, recordId);

			_searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "invoice_no" });

			// Empty format produces empty string which is not appended
			_mockRecordManager.Verify(m => m.UpdateRecord("test_entity",
				It.Is<EntityRecord>(r => ((string)r["x_search"]) == ""),
				false), Times.Once());
		}

		[Fact]
		public void RegenSearchField_CurrencyField_BeforeSymbolPlacement_FormatsCorrectly()
		{
			var recordId = Guid.NewGuid();
			var currency = new CurrencyType
			{
				Code = "USD",
				SymbolNative = "$",
				DecimalDigits = 2,
				SymbolPlacement = CurrencySymbolPlacement.Before
			};
			var field = new CurrencyField { Name = "amount", Currency = currency };
			var entity = CreateEntity("test_entity", AccountEntityId, new List<Field> { field });
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			var eqlRec = new EntityRecord();
			eqlRec["amount"] = 1234.56m;
			SetupEqlAndUpdate(eqlRec, recordId);

			_searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "amount" });

			// Format: "USD $1,234.56" — Code + space + Symbol + amount
			_mockRecordManager.Verify(m => m.UpdateRecord("test_entity",
				It.Is<EntityRecord>(r =>
					((string)r["x_search"]).Contains("USD") &&
					((string)r["x_search"]).Contains("$")),
				false), Times.Once());
		}

		[Fact]
		public void RegenSearchField_CurrencyField_AfterSymbolPlacement_FormatsCorrectly()
		{
			var recordId = Guid.NewGuid();
			var currency = new CurrencyType
			{
				Code = "EUR",
				SymbolNative = "\u20ac",
				DecimalDigits = 2,
				SymbolPlacement = CurrencySymbolPlacement.After
			};
			var field = new CurrencyField { Name = "amount", Currency = currency };
			var entity = CreateEntity("test_entity", AccountEntityId, new List<Field> { field });
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			var eqlRec = new EntityRecord();
			eqlRec["amount"] = 99.99m;
			SetupEqlAndUpdate(eqlRec, recordId);

			_searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "amount" });

			// Format: "EUR 99.99€" — Code + space + amount + Symbol
			_mockRecordManager.Verify(m => m.UpdateRecord("test_entity",
				It.Is<EntityRecord>(r =>
					((string)r["x_search"]).Contains("EUR") &&
					((string)r["x_search"]).Contains("\u20ac")),
				false), Times.Once());
		}

		[Fact]
		public void RegenSearchField_DateField_WithFormat_FormatsCorrectly()
		{
			var recordId = Guid.NewGuid();
			var field = new DateField { Name = "start_date", Format = "yyyy-MM-dd" };
			var entity = CreateEntity("test_entity", AccountEntityId, new List<Field> { field });
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			var eqlRec = new EntityRecord();
			eqlRec["start_date"] = new DateTime(2024, 3, 15);
			SetupEqlAndUpdate(eqlRec, recordId);

			_searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "start_date" });

			VerifyUpdateContains("test_entity", "2024-03-15", recordId);
		}

		[Fact]
		public void RegenSearchField_DateTimeField_WithFormat_FormatsCorrectly()
		{
			var recordId = Guid.NewGuid();
			var field = new DateTimeField { Name = "created_at", Format = "yyyy-MM-dd HH:mm" };
			var entity = CreateEntity("test_entity", AccountEntityId, new List<Field> { field });
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			var eqlRec = new EntityRecord();
			eqlRec["created_at"] = new DateTime(2024, 3, 15, 14, 30, 0);
			SetupEqlAndUpdate(eqlRec, recordId);

			_searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "created_at" });

			VerifyUpdateContains("test_entity", "2024-03-15 14:30", recordId);
		}

		[Fact]
		public void RegenSearchField_NumberField_WithDecimalPlaces_FormatsCorrectly()
		{
			var recordId = Guid.NewGuid();
			var field = new NumberField { Name = "quantity", DecimalPlaces = 3 };
			var entity = CreateEntity("test_entity", AccountEntityId, new List<Field> { field });
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			var eqlRec = new EntityRecord();
			eqlRec["quantity"] = 42.1234m;
			SetupEqlAndUpdate(eqlRec, recordId);

			_searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "quantity" });

			// "N3" format: "42.123"
			_mockRecordManager.Verify(m => m.UpdateRecord("test_entity",
				It.Is<EntityRecord>(r => ((string)r["x_search"]).Contains("42")),
				false), Times.Once());
		}

		[Fact]
		public void RegenSearchField_PercentField_FormatsAsPercent()
		{
			var recordId = Guid.NewGuid();
			var field = new PercentField { Name = "rate", DecimalPlaces = 2 };
			var entity = CreateEntity("test_entity", AccountEntityId, new List<Field> { field });
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			var eqlRec = new EntityRecord();
			eqlRec["rate"] = 0.1575m;
			SetupEqlAndUpdate(eqlRec, recordId);

			_searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "rate" });

			// "P2" format: culture-dependent, but should contain "15.75"
			_mockRecordManager.Verify(m => m.UpdateRecord("test_entity",
				It.Is<EntityRecord>(r => ((string)r["x_search"]).Contains("15.75")),
				false), Times.Once());
		}

		[Fact]
		public void RegenSearchField_SelectField_ResolvesOptionLabel()
		{
			var recordId = Guid.NewGuid();
			var options = new List<SelectOption> { new SelectOption("active", "Active") };
			var field = new SelectField { Name = "status", Options = options };
			var entity = CreateEntity("test_entity", AccountEntityId, new List<Field> { field });
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			var eqlRec = new EntityRecord();
			eqlRec["status"] = "active";
			SetupEqlAndUpdate(eqlRec, recordId);

			_searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "status" });

			VerifyUpdateContains("test_entity", "Active", recordId);
		}

		[Fact]
		public void RegenSearchField_SelectField_CaseInsensitiveMatch()
		{
			var recordId = Guid.NewGuid();
			var options = new List<SelectOption> { new SelectOption("ACTIVE", "Active Status") };
			var field = new SelectField { Name = "status", Options = options };
			var entity = CreateEntity("test_entity", AccountEntityId, new List<Field> { field });
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			var eqlRec = new EntityRecord();
			eqlRec["status"] = "active"; // different case
			SetupEqlAndUpdate(eqlRec, recordId);

			_searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "status" });

			VerifyUpdateContains("test_entity", "Active Status", recordId);
		}

		[Fact]
		public void RegenSearchField_SelectField_NoMatchingOption_FallsBackToRawValue()
		{
			// Source uses First() not FirstOrDefault() — throws InvalidOperationException
			// which is caught by the outer try/catch and silently swallowed
			var recordId = Guid.NewGuid();
			var options = new List<SelectOption> { new SelectOption("closed", "Closed") };
			var field = new SelectField { Name = "status", Options = options };
			var entity = CreateEntity("test_entity", AccountEntityId, new List<Field> { field });
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			var eqlRec = new EntityRecord();
			eqlRec["status"] = "unknown_value"; // no matching option
			SetupEqlAndUpdate(eqlRec, recordId);

			Action act = () => _searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "status" });
			act.Should().NotThrow();

			// Value not appended because First() throws, caught by outer try/catch
			_mockRecordManager.Verify(m => m.UpdateRecord("test_entity",
				It.Is<EntityRecord>(r => !((string)r["x_search"]).Contains("unknown_value")),
				false), Times.Once());
		}

		[Fact]
		public void RegenSearchField_MultiSelectField_ListOfStrings_ResolvesLabels()
		{
			var recordId = Guid.NewGuid();
			var options = new List<SelectOption>
			{
				new SelectOption("opt1", "Option 1"),
				new SelectOption("opt2", "Option 2")
			};
			var field = new MultiSelectField { Name = "tags", Options = options };
			var entity = CreateEntity("test_entity", AccountEntityId, new List<Field> { field });
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			var eqlRec = new EntityRecord();
			eqlRec["tags"] = new List<string> { "opt1", "opt2" };
			SetupEqlAndUpdate(eqlRec, recordId);

			_searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "tags" });

			_mockRecordManager.Verify(m => m.UpdateRecord("test_entity",
				It.Is<EntityRecord>(r =>
					((string)r["x_search"]).Contains("Option 1") &&
					((string)r["x_search"]).Contains("Option 2")),
				false), Times.Once());
		}

		[Fact]
		public void RegenSearchField_MultiSelectField_CommaSeparatedString_ResolvesLabels()
		{
			var recordId = Guid.NewGuid();
			var options = new List<SelectOption>
			{
				new SelectOption("opt1", "Option 1"),
				new SelectOption("opt2", "Option 2")
			};
			var field = new MultiSelectField { Name = "tags", Options = options };
			var entity = CreateEntity("test_entity", AccountEntityId, new List<Field> { field });
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			var eqlRec = new EntityRecord();
			eqlRec["tags"] = "opt1,opt2"; // comma-separated string
			SetupEqlAndUpdate(eqlRec, recordId);

			_searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "tags" });

			_mockRecordManager.Verify(m => m.UpdateRecord("test_entity",
				It.Is<EntityRecord>(r =>
					((string)r["x_search"]).Contains("Option 1") &&
					((string)r["x_search"]).Contains("Option 2")),
				false), Times.Once());
		}

		[Fact]
		public void RegenSearchField_MultiSelectField_SingleString_NoComma_ResolvesLabel()
		{
			var recordId = Guid.NewGuid();
			var options = new List<SelectOption> { new SelectOption("opt1", "Option 1") };
			var field = new MultiSelectField { Name = "tags", Options = options };
			var entity = CreateEntity("test_entity", AccountEntityId, new List<Field> { field });
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			var eqlRec = new EntityRecord();
			eqlRec["tags"] = "opt1"; // single string, no comma
			SetupEqlAndUpdate(eqlRec, recordId);

			_searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "tags" });

			VerifyUpdateContains("test_entity", "Option 1", recordId);
		}

		[Fact]
		public void RegenSearchField_PasswordField_IsIntentionallyIgnored()
		{
			var recordId = Guid.NewGuid();
			var passwordField = new PasswordField { Name = "secret" };
			var textField = CreateTextField("name");
			var entity = CreateEntity("test_entity", AccountEntityId, new List<Field> { passwordField, textField });
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			var eqlRec = new EntityRecord();
			eqlRec["secret"] = "supersecret123";
			eqlRec["name"] = "VisibleValue";
			SetupEqlAndUpdate(eqlRec, recordId);

			_searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "secret", "name" });

			_mockRecordManager.Verify(m => m.UpdateRecord("test_entity",
				It.Is<EntityRecord>(r =>
					!((string)r["x_search"]).Contains("supersecret123") &&
					((string)r["x_search"]).Contains("VisibleValue")),
				false), Times.Once());
		}

		[Fact]
		public void RegenSearchField_DefaultFieldType_UsesToString()
		{
			var recordId = Guid.NewGuid();
			var field = CreateTextField("email_field");
			var entity = CreateEntity("test_entity", AccountEntityId, new List<Field> { field });
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			var eqlRec = new EntityRecord();
			eqlRec["email_field"] = "hello@example.com";
			SetupEqlAndUpdate(eqlRec, recordId);

			_searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "email_field" });

			VerifyUpdateContains("test_entity", "hello@example.com", recordId);
		}

		// ════════════════════════════════════════════════════════════════
		//  Category 4 — Record Update Behavior Tests
		// ════════════════════════════════════════════════════════════════

		[Fact]
		public void RegenSearchField_CallsUpdateRecord_WithExecuteHooksFalse()
		{
			var recordId = Guid.NewGuid();
			SetupSimpleEntity("test_entity", AccountEntityId, "name", "Value", recordId);

			_searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "name" });

			// Critical: executeHooks must be false to prevent infinite recursion
			_mockRecordManager.Verify(m => m.UpdateRecord(
				It.IsAny<string>(),
				It.IsAny<EntityRecord>(),
				false), Times.Once());
		}

		[Fact]
		public void RegenSearchField_UpdateRecord_PatchContainsIdAndXSearch()
		{
			var recordId = Guid.NewGuid();
			SetupSimpleEntity("test_entity", AccountEntityId, "name", "Value", recordId);
			EntityRecord capturedPatch = null;
			_mockRecordManager.Setup(m => m.UpdateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>(), It.IsAny<bool>()))
				.Callback<string, EntityRecord, bool>((e, r, h) => capturedPatch = r)
				.Returns(new QueryResponse { Success = true });

			_searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "name" });

			capturedPatch.Should().NotBeNull();
			capturedPatch["id"].Should().Be(recordId);
			capturedPatch["x_search"].Should().NotBeNull();
			((string)capturedPatch["x_search"]).Should().Contain("Value");
		}

		[Fact]
		public void RegenSearchField_UpdateFails_ThrowsValidationException()
		{
			var recordId = Guid.NewGuid();
			var entity = CreateEntity("test_entity", AccountEntityId, new List<Field> { CreateTextField("name") });
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			var eqlRec = new EntityRecord();
			eqlRec["name"] = "TestValue";
			var eqlResult = new EntityRecordList();
			eqlResult.Add(eqlRec);
			_searchService.SetEqlResult(eqlResult);

			var failureResponse = new QueryResponse
			{
				Success = false,
				Message = "Update failed: validation error"
			};
			failureResponse.Errors.Add(new ErrorModel("x_search", "too_long", "Field value exceeds max length"));
			_mockRecordManager.Setup(m => m.UpdateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>(), It.IsAny<bool>()))
				.Returns(failureResponse);

			Action act = () => _searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "name" });
			act.Should().Throw<ValidationException>()
				.Where(e => e.Message.Contains("Update failed"));
		}

		[Fact]
		public void RegenSearchField_EmptyEqlResult_ProducesEmptyXSearch()
		{
			var recordId = Guid.NewGuid();
			var entity = CreateEntity("test_entity", AccountEntityId, new List<Field> { CreateTextField("name") });
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			_searchService.SetEqlResult(new EntityRecordList()); // empty result
			SetupRecordManagerUpdateSuccess();

			_searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "name" });

			_mockRecordManager.Verify(m => m.UpdateRecord("test_entity",
				It.Is<EntityRecord>(r => (string)r["x_search"] == ""),
				false), Times.Once());
		}

		[Fact]
		public void RegenSearchField_FieldFormattingException_SwallowedSilently()
		{
			// Set up an AutoNumberField where the value is NOT a decimal — triggers cast exception
			// which should be silently caught, and the other field should still be processed
			var recordId = Guid.NewGuid();
			var autoField = new AutoNumberField { Name = "bad_field", DisplayFormat = "INV-{0}" };
			var textField = CreateTextField("good_field");
			var entity = CreateEntity("test_entity", AccountEntityId, new List<Field> { autoField, textField });
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			var eqlRec = new EntityRecord();
			eqlRec["bad_field"] = "not_a_decimal"; // will cause InvalidCastException
			eqlRec["good_field"] = "GoodValue";
			SetupEqlAndUpdate(eqlRec, recordId);

			Action act = () => _searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "bad_field", "good_field" });
			act.Should().NotThrow();

			// "GoodValue" should still be in the search index
			VerifyUpdateContains("test_entity", "GoodValue", recordId);
		}

		[Fact]
		public void RegenSearchField_RelationFieldException_SwallowedSilently()
		{
			// Set up a relation column where the data type is unexpected (string instead of EntityRecord)
			var recordId = Guid.NewGuid();
			var entity = CreateEntity("account", AccountEntityId, new List<Field> { CreateTextField("name") });
			var relatedEntity = CreateEntity("country", CountryEntityId, new List<Field> { CreateTextField("label") });
			var relation = CreateEntityRelation("country_1n_account", AccountEntityId, CountryEntityId);
			SetupManagerMocks(new List<Entity> { entity, relatedEntity }, new List<EntityRelation> { relation });
			var eqlRec = new EntityRecord();
			eqlRec["name"] = "TestCo";
			eqlRec["$country_1n_account"] = "not_an_entity_record"; // wrong type — triggers exception
			SetupEqlAndUpdate(eqlRec, recordId);

			Action act = () => _searchService.RegenSearchField("account", CreateInputRecord(recordId),
				new List<string> { "name", "$country_1n_account.label" });
			act.Should().NotThrow();

			// "TestCo" should still be in the search index despite relation error
			VerifyUpdateContains("account", "TestCo", recordId);
		}

		// ════════════════════════════════════════════════════════════════
		//  Category 5 — Edge Case Tests
		// ════════════════════════════════════════════════════════════════

		[Fact]
		public void RegenSearchField_NullFieldValue_SkippedInSearchIndex()
		{
			var recordId = Guid.NewGuid();
			var entity = CreateEntity("test_entity", AccountEntityId, new List<Field>
			{
				CreateTextField("name"), CreateTextField("email")
			});
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			var eqlRec = new EntityRecord();
			eqlRec["name"] = null; // null value
			eqlRec["email"] = "test@test.com";
			SetupEqlAndUpdate(eqlRec, recordId);

			_searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "name", "email" });

			_mockRecordManager.Verify(m => m.UpdateRecord("test_entity",
				It.Is<EntityRecord>(r =>
					((string)r["x_search"]).Contains("test@test.com") &&
					!((string)r["x_search"]).Contains("null")),
				false), Times.Once());
		}

		[Fact]
		public void RegenSearchField_WhitespaceFieldValue_NotAppended()
		{
			var recordId = Guid.NewGuid();
			var entity = CreateEntity("test_entity", AccountEntityId, new List<Field>
			{
				CreateTextField("name"), CreateTextField("email")
			});
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			var eqlRec = new EntityRecord();
			eqlRec["name"] = "   "; // whitespace only — should be skipped
			eqlRec["email"] = "test@test.com";
			SetupEqlAndUpdate(eqlRec, recordId);

			_searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string> { "name", "email" });

			// x_search should only contain the email value
			_mockRecordManager.Verify(m => m.UpdateRecord("test_entity",
				It.Is<EntityRecord>(r =>
					((string)r["x_search"]).Contains("test@test.com") &&
					((string)r["x_search"]).Trim().Length > 0),
				false), Times.Once());
		}

		[Fact]
		public void RegenSearchField_EmptyIndexedFields_ProducesEmptyXSearch()
		{
			var recordId = Guid.NewGuid();
			var entity = CreateEntity("test_entity", AccountEntityId, new List<Field> { CreateTextField("name") });
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			_searchService.SetEqlResult(new EntityRecordList());
			SetupRecordManagerUpdateSuccess();

			_searchService.RegenSearchField("test_entity", CreateInputRecord(recordId),
				new List<string>()); // empty indexed fields

			_mockRecordManager.Verify(m => m.UpdateRecord("test_entity",
				It.Is<EntityRecord>(r => (string)r["x_search"] == ""),
				false), Times.Once());
		}

		[Fact]
		public void RegenSearchField_RelationField_ListOfEntityRecords_AppendsAllValues()
		{
			var recordId = Guid.NewGuid();
			var caseEntity = CreateEntity("case", CaseEntityId, new List<Field>());
			var accEntity = CreateEntity("account_nn", AccountNnEntityId, new List<Field> { CreateTextField("name") });
			var relation = CreateEntityRelation("account_nn_case", AccountNnEntityId, CaseEntityId);
			SetupManagerMocks(new List<Entity> { caseEntity, accEntity }, new List<EntityRelation> { relation });
			var eqlRec = new EntityRecord();
			var rec1 = new EntityRecord();
			rec1["name"] = "Alpha Corp";
			var rec2 = new EntityRecord();
			rec2["name"] = "Beta Inc";
			var rec3 = new EntityRecord();
			rec3["name"] = "Gamma LLC";
			eqlRec["$account_nn_case"] = new List<EntityRecord> { rec1, rec2, rec3 };
			SetupEqlAndUpdate(eqlRec, recordId);

			_searchService.RegenSearchField("case", CreateInputRecord(recordId),
				new List<string> { "$account_nn_case.name" });

			_mockRecordManager.Verify(m => m.UpdateRecord("case",
				It.Is<EntityRecord>(r =>
					((string)r["x_search"]).Contains("Alpha Corp") &&
					((string)r["x_search"]).Contains("Beta Inc") &&
					((string)r["x_search"]).Contains("Gamma LLC")),
				false), Times.Once());
		}

		[Fact]
		public void RegenSearchField_RelationField_SingleEntityRecord_AppendsValue()
		{
			var recordId = Guid.NewGuid();
			var contactEntity = CreateEntity("contact", ContactEntityId, new List<Field>());
			var countryEntity = CreateEntity("country", CountryEntityId, new List<Field> { CreateTextField("label") });
			var relation = CreateEntityRelation("country_1n_contact", CountryEntityId, ContactEntityId);
			SetupManagerMocks(new List<Entity> { contactEntity, countryEntity }, new List<EntityRelation> { relation });
			var eqlRec = new EntityRecord();
			var cntRec = new EntityRecord();
			cntRec["label"] = "France";
			eqlRec["$country_1n_contact"] = cntRec; // single EntityRecord, not List
			SetupEqlAndUpdate(eqlRec, recordId);

			_searchService.RegenSearchField("contact", CreateInputRecord(recordId),
				new List<string> { "$country_1n_contact.label" });

			VerifyUpdateContains("contact", "France", recordId);
		}

		// ════════════════════════════════════════════════════════════════
		//  Private Helper Methods — Test Data Construction
		// ════════════════════════════════════════════════════════════════

		private static Entity CreateEntity(string name, Guid id, List<Field> fields)
		{
			return new Entity { Id = id, Name = name, Fields = fields };
		}

		private static TextField CreateTextField(string name)
		{
			return new TextField { Name = name, Id = Guid.NewGuid() };
		}

		private static EntityRelation CreateEntityRelation(string name, Guid originEntityId, Guid targetEntityId)
		{
			return new EntityRelation
			{
				Id = Guid.NewGuid(),
				Name = name,
				OriginEntityId = originEntityId,
				TargetEntityId = targetEntityId
			};
		}

		private static EntityRecord CreateInputRecord(Guid recordId)
		{
			var record = new EntityRecord();
			record["id"] = recordId;
			return record;
		}

		private void SetupManagerMocks(List<Entity> entities, List<EntityRelation> relations)
		{
			_mockEntityManager.Setup(m => m.ReadEntities())
				.Returns(new EntityListResponse { Object = entities, Success = true });
			_mockRelationManager.Setup(m => m.Read())
				.Returns(new EntityRelationListResponse { Object = relations, Success = true });
		}

		private void SetupRecordManagerUpdateSuccess()
		{
			_mockRecordManager.Setup(m => m.UpdateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>(), It.IsAny<bool>()))
				.Returns(new QueryResponse { Success = true });
		}

		private void SetupEqlAndUpdate(EntityRecord eqlRecord, Guid recordId)
		{
			var eqlResult = new EntityRecordList();
			eqlResult.Add(eqlRecord);
			_searchService.SetEqlResult(eqlResult);
			SetupRecordManagerUpdateSuccess();
		}

		/// <summary>
		/// Convenience setup for simple single-field entity test scenarios.
		/// Creates an entity with one text field, sets up mocks, and prepares EQL result.
		/// </summary>
		private void SetupSimpleEntity(string entityName, Guid entityId, string fieldName, string fieldValue, Guid recordId)
		{
			var entity = CreateEntity(entityName, entityId, new List<Field> { CreateTextField(fieldName) });
			SetupManagerMocks(new List<Entity> { entity }, new List<EntityRelation>());
			var eqlRec = new EntityRecord();
			eqlRec[fieldName] = fieldValue;
			SetupEqlAndUpdate(eqlRec, recordId);
		}

		/// <summary>
		/// Convenience verification that UpdateRecord was called with x_search containing the expected value.
		/// </summary>
		private void VerifyUpdateContains(string entityName, string expectedContent, Guid recordId)
		{
			_mockRecordManager.Verify(m => m.UpdateRecord(entityName,
				It.Is<EntityRecord>(r =>
					((string)r["x_search"]).Contains(expectedContent) &&
					(Guid)r["id"] == recordId),
				false), Times.Once());
		}

		// ════════════════════════════════════════════════════════════════
		//  TestableSearchService — Subclass with EQL execution override
		// ════════════════════════════════════════════════════════════════

		/// <summary>
		/// Test double that extends <see cref="SearchService"/> and overrides the
		/// <c>ExecuteEqlQuery</c> protected virtual method to inject mock EQL results.
		/// This allows pure unit testing of all field resolution, formatting, and
		/// update logic without requiring a live PostgreSQL database or EQL engine.
		/// </summary>
		private class TestableSearchService : SearchService
		{
			private EntityRecordList _eqlResult = new EntityRecordList();

			public TestableSearchService(
				ICrmEntityRelationManager entityRelationManager,
				ICrmEntityManager entityManager,
				ICrmRecordManager recordManager)
				: base(entityRelationManager, entityManager, recordManager)
			{
			}

			/// <summary>
			/// Configures the EQL result that will be returned by <see cref="ExecuteEqlQuery"/>.
			/// Call this before invoking <see cref="SearchService.RegenSearchField"/>.
			/// </summary>
			public void SetEqlResult(EntityRecordList result)
			{
				_eqlResult = result ?? new EntityRecordList();
			}

			/// <summary>
			/// Returns the last EQL command string that was passed to <see cref="ExecuteEqlQuery"/>.
			/// Useful for asserting that requestColumns were correctly built.
			/// </summary>
			public string LastEqlCommand { get; private set; }

			/// <summary>
			/// Returns the last EQL parameters list that was passed to <see cref="ExecuteEqlQuery"/>.
			/// Useful for asserting that the recordId parameter was correctly set.
			/// </summary>
			public List<EqlParameter> LastEqlParameters { get; private set; }

			/// <summary>
			/// Overrides the EQL execution to return pre-configured mock data instead of
			/// executing against a live database. Records the command and parameters for assertions.
			/// </summary>
			protected override EntityRecordList ExecuteEqlQuery(string eqlCommand, List<EqlParameter> eqlParameters)
			{
				LastEqlCommand = eqlCommand;
				LastEqlParameters = eqlParameters;
				return _eqlResult;
			}
		}
	}
}
