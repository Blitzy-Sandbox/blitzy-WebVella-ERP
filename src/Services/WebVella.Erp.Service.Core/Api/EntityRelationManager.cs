using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Configuration;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.SharedKernel.Utilities;
using WebVella.Erp.Service.Core.Database;

namespace WebVella.Erp.Service.Core.Api
{
	/// <summary>
	/// CRUD and validation for EntityRelation metadata, including immutability rules on updates,
	/// uniqueness constraints, relation-type integrity (1:1/1:N/N:N), and GUID-field requirements.
	/// Adapted from WebVella.Erp/Api/EntityRelationManager.cs.
	/// This is a compilable scaffold — the assigned agent will provide the full implementation.
	/// </summary>
	public class EntityRelationManager
	{
		private readonly CoreDbContext _dbContext;
		private readonly IConfiguration _configuration;

		private bool IsDevelopmentMode =>
			string.Equals(_configuration["Settings:DevelopmentMode"], "true", StringComparison.OrdinalIgnoreCase);

		public EntityRelationManager(CoreDbContext dbContext, IConfiguration configuration)
		{
			_dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}

		#region << Validation >>

		private enum ValidationType
		{
			Create,
			Update,
			RelationsOnly
		}

		private List<ErrorModel> ValidateRelation(EntityRelation relation, ValidationType validationType)
		{
			List<ErrorModel> errors = new List<ErrorModel>();

			if (relation.Id == Guid.Empty)
				errors.Add(new ErrorModel("id", null, "Id is required!"));

			errors.AddRange(ValidationUtility.ValidateName(relation.Name));
			errors.AddRange(ValidationUtility.ValidateLabel(relation.Label));

			if (validationType == ValidationType.Create)
			{
				// Check for existing relation with same name
				var existingRelation = Read(relation.Name).Object;
				if (existingRelation != null)
					errors.Add(new ErrorModel("name", relation.Name, "Relation with such Name exists already!"));

				// Check for existing relation with same Id
				if (relation.Id != Guid.Empty && Read(relation.Id).Object != null)
					errors.Add(new ErrorModel("id", relation.Id.ToString(), "Relation with such Id exists already!"));
			}

			// Validate origin entity and field
			var entityManager = new EntityManager(_dbContext, _configuration);
			var originEntity = entityManager.ReadEntity(relation.OriginEntityId).Object;
			if (originEntity == null)
				errors.Add(new ErrorModel("originEntityId", relation.OriginEntityId.ToString(), "Origin entity not found!"));
			else
			{
				var originField = originEntity.Fields.FirstOrDefault(f => f.Id == relation.OriginFieldId);
				if (originField == null)
					errors.Add(new ErrorModel("originFieldId", relation.OriginFieldId.ToString(), "Origin field not found!"));
				else if (!(originField is GuidField))
					errors.Add(new ErrorModel("originFieldId", relation.OriginFieldId.ToString(), "Origin field must be Unique Identifier (GUID) field!"));
			}

			// Validate target entity and field
			var targetEntity = entityManager.ReadEntity(relation.TargetEntityId).Object;
			if (targetEntity == null)
				errors.Add(new ErrorModel("targetEntityId", relation.TargetEntityId.ToString(), "Target entity not found!"));
			else
			{
				var targetField = targetEntity.Fields.FirstOrDefault(f => f.Id == relation.TargetFieldId);
				if (targetField == null)
					errors.Add(new ErrorModel("targetFieldId", relation.TargetFieldId.ToString(), "Target field not found!"));
				else if (!(targetField is GuidField))
					errors.Add(new ErrorModel("targetFieldId", relation.TargetFieldId.ToString(), "Target field must be Unique Identifier (GUID) field!"));
			}

			// Validate immutability on updates
			if (validationType == ValidationType.Update)
			{
				var existingRelation = Read(relation.Id).Object;
				if (existingRelation != null)
				{
					if (existingRelation.RelationType != relation.RelationType)
						errors.Add(new ErrorModel("relationType", relation.RelationType.ToString(), "Relation type cannot be changed!"));
					if (existingRelation.OriginEntityId != relation.OriginEntityId)
						errors.Add(new ErrorModel("originEntityId", relation.OriginEntityId.ToString(), "Origin entity cannot be changed!"));
					if (existingRelation.OriginFieldId != relation.OriginFieldId)
						errors.Add(new ErrorModel("originFieldId", relation.OriginFieldId.ToString(), "Origin field cannot be changed!"));
					if (existingRelation.TargetEntityId != relation.TargetEntityId)
						errors.Add(new ErrorModel("targetEntityId", relation.TargetEntityId.ToString(), "Target entity cannot be changed!"));
					if (existingRelation.TargetFieldId != relation.TargetFieldId)
						errors.Add(new ErrorModel("targetFieldId", relation.TargetFieldId.ToString(), "Target field cannot be changed!"));
				}
			}

			// Check unique/required constraints based on relation type
			if (validationType != ValidationType.RelationsOnly && originEntity != null && targetEntity != null)
			{
				var originField = originEntity.Fields.FirstOrDefault(f => f.Id == relation.OriginFieldId);
				var targetField = targetEntity.Fields.FirstOrDefault(f => f.Id == relation.TargetFieldId);

				if (originField != null && targetField != null)
				{
					switch (relation.RelationType)
					{
						case EntityRelationType.OneToOne:
							if (!originField.Required)
								errors.Add(new ErrorModel("originFieldId", relation.OriginFieldId.ToString(), "Origin field must be required for 1:1 relation!"));
							if (!originField.Unique)
								errors.Add(new ErrorModel("originFieldId", relation.OriginFieldId.ToString(), "Origin field must be unique for 1:1 relation!"));
							if (!targetField.Required)
								errors.Add(new ErrorModel("targetFieldId", relation.TargetFieldId.ToString(), "Target field must be required for 1:1 relation!"));
							if (!targetField.Unique)
								errors.Add(new ErrorModel("targetFieldId", relation.TargetFieldId.ToString(), "Target field must be unique for 1:1 relation!"));
							break;
						case EntityRelationType.OneToMany:
							if (!originField.Required)
								errors.Add(new ErrorModel("originFieldId", relation.OriginFieldId.ToString(), "Origin field must be required for 1:N relation!"));
							if (!originField.Unique)
								errors.Add(new ErrorModel("originFieldId", relation.OriginFieldId.ToString(), "Origin field must be unique for 1:N relation!"));
							break;
						case EntityRelationType.ManyToMany:
							// No special constraints for N:N
							break;
					}
				}
			}

			return errors;
		}

		#endregion

		#region << Read methods >>

		public EntityRelationResponse Read(Guid relationId)
		{
			EntityRelationResponse response = new EntityRelationResponse
			{
				Success = true,
				Message = "The relation was successfully returned!",
			};

			try
			{
				List<EntityRelation> relations = Read().Object;
				EntityRelation relation = relations?.FirstOrDefault(r => r.Id == relationId);
				if (relation != null)
					response.Object = relation;
			}
			catch (Exception e)
			{
				response.Success = false;
				response.Timestamp = DateTime.UtcNow;
				if (IsDevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "An internal error occurred!";
			}

			response.Timestamp = DateTime.UtcNow;
			return response;
		}

		public EntityRelationResponse Read(string name)
		{
			EntityRelationResponse response = new EntityRelationResponse
			{
				Success = true,
				Message = "The relation was successfully returned!",
			};

			try
			{
				List<EntityRelation> relations = Read().Object;
				EntityRelation relation = relations?.FirstOrDefault(r => r.Name == name);
				if (relation != null)
					response.Object = relation;
			}
			catch (Exception e)
			{
				response.Success = false;
				response.Timestamp = DateTime.UtcNow;
				if (IsDevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "An internal error occurred!";
			}

			response.Timestamp = DateTime.UtcNow;
			return response;
		}

		public EntityRelationListResponse Read()
		{
			EntityRelationListResponse response = new EntityRelationListResponse
			{
				Success = true,
				Message = "The relations were successfully returned!",
			};

			// Try cache first
			var relations = Cache.GetRelations();
			if (relations != null)
			{
				response.Object = relations;
				response.Hash = Cache.GetRelationsHash();
				return response;
			}

			try
			{
				lock (EntityManager.lockObj)
				{
					// Double-check cache after acquiring lock
					relations = Cache.GetRelations();
					if (relations != null)
					{
						response.Object = relations;
						response.Hash = Cache.GetRelationsHash();
						return response;
					}

					var dbRelations = _dbContext.RelationRepository.Read();
					relations = dbRelations.Select(x => x.MapTo<EntityRelation>()).ToList();

					Cache.AddRelations(relations);
					response.Object = relations;
					response.Hash = Cache.GetRelationsHash();
				}
			}
			catch (Exception e)
			{
				response.Success = false;
				response.Timestamp = DateTime.UtcNow;
				if (IsDevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "An internal error occurred!";
				return response;
			}

			response.Timestamp = DateTime.UtcNow;
			return response;
		}

		#endregion

		#region << Create/Update/Delete >>

		public EntityRelationResponse Create(EntityRelation relation)
		{
			EntityRelationResponse response = new EntityRelationResponse
			{
				Success = true,
				Message = "The relation was successfully created!",
			};

			bool hasPermission = SecurityContext.HasMetaPermission();
			if (!hasPermission)
			{
				response.StatusCode = HttpStatusCode.Forbidden;
				response.Success = false;
				response.Message = "No permissions to manipulate erp meta.";
				response.Errors.Add(new ErrorModel { Message = "Access denied." });
				return response;
			}

			try
			{
				if (relation.Id == Guid.Empty)
					relation.Id = Guid.NewGuid();

				response.Errors = ValidateRelation(relation, ValidationType.Create);
				response.Object = relation;

				if (response.Errors.Count > 0)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "The relation was not created. Validation error occurred!";
					return response;
				}

				var storageRelation = relation.MapTo<DbEntityRelation>();
				bool result = _dbContext.RelationRepository.Create(storageRelation);
				if (!result)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "The relation was not created! An internal error occurred!";
					return response;
				}
			}
			catch (Exception e)
			{
				Cache.Clear();
				response.Success = false;
				response.Object = relation;
				response.Timestamp = DateTime.UtcNow;
				if (IsDevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "The relation was not created. An internal error occurred!";
				return response;
			}

			Cache.Clear();

			response.Object = Read(relation.Id).Object ?? relation;
			response.Timestamp = DateTime.UtcNow;
			return response;
		}

		public EntityRelationResponse Update(EntityRelation relation)
		{
			EntityRelationResponse response = new EntityRelationResponse
			{
				Success = true,
				Message = "The relation was successfully updated!",
			};

			bool hasPermission = SecurityContext.HasMetaPermission();
			if (!hasPermission)
			{
				response.StatusCode = HttpStatusCode.Forbidden;
				response.Success = false;
				response.Message = "No permissions to manipulate erp meta.";
				response.Errors.Add(new ErrorModel { Message = "Access denied." });
				return response;
			}

			try
			{
				response.Errors = ValidateRelation(relation, ValidationType.Update);
				response.Object = relation;

				if (response.Errors.Count > 0)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "The relation was not updated. Validation error occurred!";
					return response;
				}

				var storageRelation = relation.MapTo<DbEntityRelation>();
				bool result = _dbContext.RelationRepository.Update(storageRelation);
				if (!result)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "The relation was not updated! An internal error occurred!";
					return response;
				}
			}
			catch (Exception e)
			{
				Cache.Clear();
				response.Success = false;
				response.Object = relation;
				response.Timestamp = DateTime.UtcNow;
				if (IsDevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "The relation was not updated. An internal error occurred!";
				return response;
			}

			Cache.Clear();

			response.Object = Read(relation.Id).Object ?? relation;
			response.Timestamp = DateTime.UtcNow;
			return response;
		}

		public EntityRelationResponse Delete(Guid relationId)
		{
			EntityRelationResponse response = new EntityRelationResponse
			{
				Success = true,
				Message = "The relation was successfully deleted!",
			};

			bool hasPermission = SecurityContext.HasMetaPermission();
			if (!hasPermission)
			{
				response.StatusCode = HttpStatusCode.Forbidden;
				response.Success = false;
				response.Message = "No permissions to manipulate erp meta.";
				response.Errors.Add(new ErrorModel { Message = "Access denied." });
				return response;
			}

			try
			{
				var storageRelation = _dbContext.RelationRepository.Read(relationId);
				if (storageRelation == null)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "The relation was not deleted. Validation error occurred!";
					response.Errors.Add(new ErrorModel("id", relationId.ToString(), "Relation with such Id does not exist!"));
					return response;
				}

				_dbContext.RelationRepository.Delete(relationId);
			}
			catch (Exception e)
			{
				Cache.Clear();
				response.Success = false;
				response.Timestamp = DateTime.UtcNow;
				if (IsDevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "The relation was not deleted. An internal error occurred!";
				return response;
			}

			Cache.Clear();

			response.Timestamp = DateTime.UtcNow;
			return response;
		}

		#endregion
	}
}
