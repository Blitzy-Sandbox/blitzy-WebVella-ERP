using Microsoft.Extensions.Configuration;
using System;
using System.Globalization;
using WebVella.Erp.Api;
using WebVella.Erp.Api.Models;
using WebVella.Erp.Api.Models.AutoMapper;
using WebVella.Erp.Database;
using WebVella.Erp.Eql;
using WebVella.Erp.Hooks;

namespace WebVella.Erp.ConsoleApp
{
	class Program
	{
		static void Main()
		{
			using (var secCtx = SecurityContext.OpenSystemScope())
			{
				//call this method once to initialize erp engine
				InitErpEngine();

				var usersRecordList = SampleGetAllErpUsers();

				Console.WriteLine($"=== existing users ( filtered by pre search hook by current user id ) ===");
				Console.WriteLine($"=== should return only current user ===");
				foreach (var rec in usersRecordList)
					Console.WriteLine($"username:{rec["username"]} \t\t email:{rec["email"]}");

				RecordHookSample();
			}
		}

		private static void InitErpEngine()
		{
			CultureInfo customCulture = new CultureInfo("en-US");
			customCulture.NumberFormat.NumberDecimalSeparator = ".";
			CultureInfo.DefaultThreadCurrentCulture = customCulture;
			CultureInfo.DefaultThreadCurrentUICulture = customCulture;

			// SECURITY (OWASP A02/A05 - secret externalization, AAP 0.4.2): layer environment variables on
			// top of the committed config.json so runtime secret overlays (e.g. Settings__EncryptionKey)
			// reach ErpSettings.Initialize, consistent with ErpMvcExtensions.UseErp and the web-host Startups.
			// Environment variables are added LAST so they take precedence over the JSON placeholders, which
			// is exactly what the Config.json EncryptionKey note describes ("set at runtime via environment
			// variable Settings__EncryptionKey or a secret store").
			var configurationBuilder = new ConfigurationBuilder()
				.AddJsonFile("config.json".ToApplicationPath())
				.AddEnvironmentVariables();
			ErpSettings.Initialize(configurationBuilder.Build());
			DbContext.CreateContext(ErpSettings.ConnectionString);
			ErpService service = new ErpService();
            
			ErpAutoMapperConfiguration.Configure(ErpAutoMapperConfiguration.MappingExpressions);
            //here put additional automapper configuration if needed
            ErpAutoMapper.Initialize(ErpAutoMapperConfiguration.MappingExpressions);

            service.InitializeSystemEntities();
			

			//register hooks
			HookManager.RegisterHooks(service);

			DbContext.CloseContext();
		}

		private static EntityRecordList SampleGetAllErpUsers()
		{
			EntityRecordList result = null;

			//you need to create manually database context
			using (var dbCtx = DbContext.CreateContext(ErpSettings.ConnectionString))
			{
				//create connection
				using (var connection = dbCtx.CreateConnection())
				{
					//create security context - in this sample we use OpenSystemScope method, 
					//which used system user with all privileges and rights to erp data
					using (var scope = SecurityContext.OpenSystemScope())
					{
						try
						{
							//use transaction if needed
							connection.BeginTransaction();

							result = new EqlCommand("SELECT * FROM user").Execute();

							connection.CommitTransaction();
						}
						catch
						{
							connection.RollbackTransaction();
							throw;
						}
					}
				}
			}
			return result;
		}

		private static void RecordHookSample()
		{
			//you need to create manually database context
			using (var dbCtx = DbContext.CreateContext(ErpSettings.ConnectionString))
			{
				//create connection
				using (var connection = dbCtx.CreateConnection())
				{
					//create security context - in this sample we use OpenSystemScope method, 
					//which used system user with all privileges and rights to erp data
					using (var scope = SecurityContext.OpenSystemScope())
					{
						try
						{
							connection.BeginTransaction();

							RecordManager recMan = new RecordManager();

							//list all records from role entity
							var existingRoles = new EqlCommand("SELECT * FROM role").Execute();
							Console.WriteLine();
							Console.WriteLine($"=== existing roles ===");
							foreach (var rec in existingRoles)
								Console.WriteLine($"name:{rec["name"]}");

							//create new role record to triger record hook
							EntityRecord newRec = new EntityRecord();
							newRec["id"] = Guid.NewGuid();
							newRec["name"] = "New Role";
							var result = recMan.CreateRecord("role", newRec);
							if (!result.Success)
								throw new Exception(result.Message);

							Console.WriteLine($"=== roles after create ===");
							existingRoles = new EqlCommand("SELECT * FROM role").Execute();
							foreach (var rec in existingRoles)
								Console.WriteLine($"name:{rec["name"]}");


							newRec["name"] = "New changed Role";
							result = recMan.UpdateRecord("role", newRec);
							if (!result.Success)
								throw new Exception(result.Message);

							Console.WriteLine($"=== roles after update ===");
							existingRoles = new EqlCommand("SELECT * FROM role").Execute();
							foreach (var rec in existingRoles)
								Console.WriteLine($"name:{rec["name"]}");

							result = recMan.DeleteRecord("role", (Guid)newRec["id"]);
							if (!result.Success)
								throw new Exception(result.Message);

							Console.WriteLine($"=== roles after delete ===");
							existingRoles = new EqlCommand("SELECT * FROM role").Execute();
							foreach (var rec in existingRoles)
								Console.WriteLine($"name:{rec["name"]}");

						}
						finally
						{
							//we allways rollback transaction - this method is only for presentation how hooks are triggered from console app
							connection.RollbackTransaction();
						}
					}
				}
			}
		}
	}
}
