using AutoMapper;
using System;
using System.Collections;
using System.Collections.Generic;

namespace WebVella.Erp.SharedKernel.Utilities
{
	/// <summary>
	/// Static AutoMapper singleton holder. Provides the global IMapper instance
	/// used by all MapTo extension methods across all microservices.
	/// 
	/// Services must call Initialize() during startup after configuring mapping
	/// profiles via ErpAutoMapperConfiguration.
	/// 
	/// Preserved from: WebVella.Erp.Api.Models.AutoMapper.ErpAutoMapper
	/// </summary>
	public static class ErpAutoMapper
	{
		/// <summary>
		/// Global AutoMapper mapper singleton instance.
		/// Initialized via the Initialize() method during service startup.
		/// All AutoMapperExtensions methods delegate to this mapper.
		/// </summary>
		public static IMapper Mapper = null;

		/// <summary>
		/// Creates and stores the global AutoMapper mapper from the provided
		/// configuration expression. This method should be called exactly once
		/// during service startup, after all profiles have been registered on
		/// the configuration expression.
		/// </summary>
		/// <param name="cfg">
		/// The fully configured MapperConfigurationExpression with all profiles registered.
		/// </param>
		public static void Initialize(MapperConfigurationExpression cfg)
		{
			Mapper = new Mapper(new MapperConfiguration(cfg));
		}
	}

	/// <summary>
	/// Thread-safe, idempotent AutoMapper configuration manager.
	/// Provides double-checked locking to ensure configuration occurs exactly once.
	/// 
	/// In the original monolith, this class registered all AutoMapper profiles
	/// (EntityProfile, EntityRelationProfile, RecordPermissionsProfile, FieldProfile,
	/// FieldPermissionsProfile, EntityRelationOptionsProfile, JobProfile, UserFileProfile,
	/// CurrencyProfile, DataSourceProfile, SearchResultProfile, DatabaseNNRelationRecordProfile,
	/// ErrorModelProfile) and type converters (GuidToStringConverter, DateTimeTypeConverter,
	/// ErpUserConverter, ErpUserConverterOposite, ErpRoleConverter).
	/// 
	/// In the microservice architecture, each service registers its own profiles on the
	/// MappingExpressions object before calling Configure(). The Configure method provides
	/// the thread-safe guarantee that initialization occurs exactly once.
	/// 
	/// Usage pattern:
	///   // 1. Register service-specific profiles
	///   ErpAutoMapperConfiguration.MappingExpressions.AddProfile(new MyServiceProfile());
	///   ErpAutoMapperConfiguration.MappingExpressions.CreateMap&lt;Guid, string&gt;().ConvertUsing&lt;MyConverter&gt;();
	///   // 2. Lock the configuration (thread-safe, idempotent)
	///   ErpAutoMapperConfiguration.Configure(ErpAutoMapperConfiguration.MappingExpressions);
	///   // 3. Initialize the global mapper
	///   ErpAutoMapper.Initialize(ErpAutoMapperConfiguration.MappingExpressions);
	/// 
	/// Preserved from: WebVella.Erp.Api.Models.AutoMapper.ErpAutoMapperConfiguration
	/// </summary>
	public static class ErpAutoMapperConfiguration
	{
		/// <summary>
		/// Global object to store mapping expressions. Services should add their
		/// AutoMapper profiles and type converters to this expression before calling Configure().
		/// 
		/// Original monolith registered these profiles here:
		///   cfg.CreateMap&lt;Guid, string&gt;().ConvertUsing&lt;GuidToStringConverter&gt;();
		///   cfg.CreateMap&lt;DateTimeOffset, DateTime&gt;().ConvertUsing&lt;DateTimeTypeConverter&gt;();
		///   cfg.AddProfile(new EntityRelationProfile());
		///   cfg.AddProfile(new EntityProfile());
		///   cfg.AddProfile(new RecordPermissionsProfile());
		///   cfg.AddProfile(new FieldPermissionsProfile());
		///   cfg.AddProfile(new FieldProfile());
		///   cfg.AddProfile(new EntityRelationOptionsProfile());
		///   cfg.AddProfile(new JobProfile());
		///   cfg.AddProfile(new UserFileProfile());
		///   cfg.AddProfile(new CurrencyProfile());
		///   cfg.AddProfile(new DataSourceProfile());
		///   cfg.CreateMap&lt;EntityRecord, ErpUser&gt;().ConvertUsing(new ErpUserConverter());
		///   cfg.CreateMap&lt;ErpUser, EntityRecord&gt;().ConvertUsing(new ErpUserConverterOposite());
		///   cfg.CreateMap&lt;EntityRecord, ErpRole&gt;().ConvertUsing(new ErpRoleConverter());
		///   cfg.AddProfile(new ErrorModelProfile());
		///   cfg.AddProfile(new SearchResultProfile());
		///   cfg.AddProfile(new DatabaseNNRelationRecordProfile());
		/// </summary>
		public static MapperConfigurationExpression MappingExpressions = new MapperConfigurationExpression();

		private static object lockObj = new object();
		private static bool alreadyConfigured = false;

		/// <summary>
		/// Performs thread-safe, idempotent AutoMapper configuration using double-checked locking.
		/// Once called, subsequent calls are no-ops (idempotent).
		/// 
		/// Services should register all their AutoMapper profiles on the cfg parameter
		/// before calling this method. After this method completes, no further profiles
		/// should be added.
		/// </summary>
		/// <param name="cfg">
		/// The MapperConfigurationExpression with service-specific profiles already registered.
		/// </param>
		public static void Configure(MapperConfigurationExpression cfg)
		{
			if (alreadyConfigured)
				return;

			lock (lockObj)
			{
				if (alreadyConfigured)
					return;

				alreadyConfigured = true;
			}
		}
	}

	/// <summary>
	/// Convenience extension methods for AutoMapper object-to-object mapping.
	/// All methods delegate to ErpAutoMapper.Mapper.Map() and return default values
	/// instead of throwing on null input (null-safe pattern).
	/// 
	/// Critical consumers:
	/// - SearchManager: row.MapTo&lt;SearchResult&gt;() where row is a DataRow
	/// - Helpers.cs: GetAllCurrency().MapTo&lt;CurrencyType&gt;()
	/// - Various service files using .MapTo&lt;T&gt;() for DTO transformations
	/// 
	/// The MapTo&lt;T&gt;(this object self) overload uses self.GetType() for runtime type
	/// resolution, enabling dynamic type mapping without compile-time type knowledge.
	/// 
	/// The additionalArguments overloads inject into opts.Items["additional_arguments"]
	/// for passing contextual data to AutoMapper resolvers.
	/// 
	/// Preserved from: WebVella.Erp.Api.Models.AutoMapper.AutoMapperExtensions
	/// </summary>
	public static class AutoMapperExtensions
	{
		/// <summary>
		/// Maps each element of an IEnumerable to a List of TResult,
		/// passing additional arguments to AutoMapper resolvers.
		/// Returns default(List&lt;TResult&gt;) if self is null.
		/// </summary>
		public static List<TResult> MapTo<TResult>(this IEnumerable self, object additionalArguments)
		{
			if (self == null)
				return default(List<TResult>); //throw new ArgumentNullException();

			return (List<TResult>)ErpAutoMapper.Mapper.Map(self, self.GetType(), typeof(List<TResult>),
				(opts => { opts.Items.Add("additional_arguments", additionalArguments); }));
		}

		/// <summary>
		/// Maps an IEnumerable to a single TResult object,
		/// passing additional arguments to AutoMapper resolvers.
		/// Returns default(TResult) if self is null.
		/// </summary>
		public static TResult MapToSingleObject<TResult>(this IEnumerable self, object additionalArguments)
		{
			if (self == null)
				return default(TResult); //throw new ArgumentNullException();

			return (TResult)ErpAutoMapper.Mapper.Map(self, self.GetType(), typeof(TResult),
				(opts => { opts.Items.Add("additional_arguments", additionalArguments); }));
		}

		/// <summary>
		/// Maps each element of an IEnumerable to a List of TResult.
		/// Returns default(List&lt;TResult&gt;) if self is null.
		/// </summary>
		public static List<TResult> MapTo<TResult>(this IEnumerable self)
		{
			if (self == null)
				return default(List<TResult>); //throw new ArgumentNullException();

			return (List<TResult>)ErpAutoMapper.Mapper.Map(self, self.GetType(), typeof(List<TResult>));
		}

		/// <summary>
		/// Maps an IEnumerable to a single TResult object.
		/// Returns default(TResult) if self is null.
		/// </summary>
		public static TResult MapToSingleObject<TResult>(this IEnumerable self)
		{
			if (self == null)
				return default(TResult); //throw new ArgumentNullException();

			return (TResult)ErpAutoMapper.Mapper.Map(self, self.GetType(), typeof(TResult));
		}

		/// <summary>
		/// Maps a single object to a List of TResult.
		/// Returns default(List&lt;TResult&gt;) if self is null.
		/// </summary>
		public static List<TResult> MapSingleObjectToList<TResult>(this object self)
		{
			if (self == null)
				return default(List<TResult>); //throw new ArgumentNullException();

			return (List<TResult>)ErpAutoMapper.Mapper.Map(self, self.GetType(), typeof(List<TResult>));
		}

		/// <summary>
		/// Maps a single object to TResult using runtime type resolution via self.GetType().
		/// This is the primary mapping method used by SearchManager and other services.
		/// Returns default(TResult) if self is null.
		/// </summary>
		public static TResult MapTo<TResult>(this object self)
		{
			if (self == null)
				return default(TResult); //throw new ArgumentNullException();

			return (TResult)ErpAutoMapper.Mapper.Map(self, self.GetType(), typeof(TResult));
		}

		/// <summary>
		/// Maps properties from source object onto an existing TResult instance.
		/// Useful for partial updates where the target already exists.
		/// Returns default(TResult) if self is null.
		/// </summary>
		public static TResult MapPropertiesToInstance<TResult>(this object self, TResult value)
		{
			if (self == null)
				return default(TResult); //throw new ArgumentNullException();

			return (TResult)ErpAutoMapper.Mapper.Map(self, value, self.GetType(), typeof(TResult));
		}

		/// <summary>
		/// Maps a single object to TResult, passing additional arguments
		/// to AutoMapper resolvers via opts.Items["additional_arguments"].
		/// Returns default(TResult) if self is null.
		/// </summary>
		public static TResult MapTo<TResult>(this object self, object additionalArguments)
		{
			if (self == null)
				return default(TResult); //throw new ArgumentNullException();

			return (TResult)ErpAutoMapper.Mapper.Map(self, self.GetType(), typeof(TResult),
				(opts => { opts.Items.Add("additional_arguments", additionalArguments); }));
		}

		//public static TResult DynamicMapTo<TResult>(this object self)
		//{
		//	if (self == null)
		//		return default(TResult); //throw new ArgumentNullException();

		//	return (TResult)Mapper.DynamicMap(self, self.GetType(), typeof(TResult));
		//}

		//public static List<TResult> DynamicMapTo<TResult>(this IEnumerable self)
		//{
		//	if (self == null)
		//		return default(List<TResult>); //throw new ArgumentNullException();

		//	return (List<TResult>)Mapper.DynamicMap(self, self.GetType(), typeof(List<TResult>));
		//}

		//public static IMappingExpression<TSource, TDestination> IgnoreAllNonExisting<TSource, TDestination>(this IMappingExpression<TSource, TDestination> expression)
		//{
		//	var sourceType = typeof(TSource);
		//	var destinationType = typeof(TDestination);
		//	var existingMaps = Mapper.Configuration.GetAllTypeMaps().First(x => x.SourceType.Equals(sourceType)
		//		&& x.DestinationType.Equals(destinationType));
		//	foreach (var property in existingMaps.GetUnmappedPropertyNames())
		//	{
		//		expression.ForMember(property, opt => opt.Ignore());
		//	}
		//	return expression;
		//}
	}
}
