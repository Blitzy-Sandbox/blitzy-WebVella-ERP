using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.Service.Admin.Models;
using WebVella.Erp.Service.Admin.Services;

namespace WebVella.Erp.Service.Admin.Controllers
{
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	public class AdminController : Controller
	{
		private const char RELATION_SEPARATOR = '.';
		private const char RELATION_NAME_RESULT_SEPARATOR = '$';

		private readonly IAppService _appService;
		private readonly IPageService _pageService;
		private readonly IDataSourceManager _dataSourceManager;
		private readonly IEntityManager _entityManager;
		private readonly IRecordManager _recordManager;
		private readonly ISecurityManager _securityManager;
		private readonly IEntityRelationManager _entityRelationManager;

		public AdminController(
			IAppService appService,
			IPageService pageService,
			IDataSourceManager dataSourceManager,
			IEntityManager entityManager,
			IRecordManager recordManager,
			ISecurityManager securityManager,
			IEntityRelationManager entityRelationManager)
		{
			_appService = appService;
			_pageService = pageService;
			_dataSourceManager = dataSourceManager;
			_entityManager = entityManager;
			_recordManager = recordManager;
			_securityManager = securityManager;
			_entityRelationManager = entityRelationManager;
		}

		#region << Data Source >>
		//[AllowAnonymous] //Just for webcomponent dev
		[Route("api/v3.0/p/sdk/datasource/list")]
		[HttpGet]
		public ActionResult DataSourceAction()
		{
			var dsList = _dataSourceManager.GetAll();
			dsList = dsList.OrderBy(x => x.Name).ToList();
			return Json(dsList);
		}


		#endregion

		#region << Sitemap Area >>
		[Authorize(Roles = "administrator")]
		[AcceptVerbs(new[] { "POST" }, Route = "api/v3.0/p/sdk/sitemap/area")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult CreateSitemapArea([FromBody]SitemapArea area, [FromQuery]Guid? appId = null)
		{
			var response = new ResponseModel();
			response.Message = "Success";
			response.Success = true;

			if (area == null)
			{
				response.Message = "Wrong object model submitted. Could not restore!";
				response.Success = false;
				return Json(response);
			}
			if (appId == null)
			{
				response.Message = "Application Id needs to be submitted as 'appId' query string";
				response.Success = false;
				return Json(response);
			}

			if (area.Id == Guid.Empty)
			{
				area.Id = Guid.NewGuid();
			}

			try
			{
				_appService.CreateArea(area.Id, appId ?? Guid.Empty, area.Name, area.Label, area.Description,
					area.IconClass, area.Color, area.ShowGroupNames, area.Weight, area.Access);
			}
			catch (Exception ex)
			{
				response.Message = ex.Message;
				response.Success = false;
				return Json(response);
			}

			var newSitemap = _appService.GetApplication(appId ?? Guid.Empty).Sitemap;
			var initData = new EntityRecord();
			initData["sitemap"] = _appService.OrderSitemap(newSitemap);
			initData["node_page_dict"] = _pageService.GetNodePageDictionary(appId);
			response.Object = initData;

			return Json(response);
		}

		[Authorize(Roles = "administrator")]
		[AcceptVerbs(new[] { "POST" }, Route = "api/v3.0/p/sdk/sitemap/area/{areaId}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult UpdateSitemapArea([FromBody]SitemapArea area, [FromQuery]Guid? appId = null)
		{
			var response = new ResponseModel();
			response.Message = "Success";
			response.Success = true;

			if (area == null)
			{
				response.Message = "Wrong object model submitted. Could not restore!";
				response.Success = false;
				return Json(response);
			}
			if (appId == null)
			{
				response.Message = "Application Id needs to be submitted as 'appId' query string";
				response.Success = false;
				return Json(response);
			}

			if (area.Id == Guid.Empty)
			{
				response.Message = "Area Id needs to be submitted";
				response.Success = false;
				return Json(response);
			}

			try
			{
				_appService.UpdateArea(area.Id, appId ?? Guid.Empty, area.Name, area.Label, area.Description,
					area.IconClass, area.Color, area.ShowGroupNames, area.Weight, area.Access);
			}
			catch (Exception ex)
			{
				response.Message = ex.Message;
				response.Success = false;
				return Json(response);
			}

			var newSitemap = _appService.GetApplication(appId ?? Guid.Empty).Sitemap;
			var initData = new EntityRecord();
			initData["sitemap"] = _appService.OrderSitemap(newSitemap);
			initData["node_page_dict"] = _pageService.GetNodePageDictionary(appId);
			response.Object = initData;

			return Json(response);
		}

		[Authorize(Roles = "administrator")]
		[AcceptVerbs(new[] { "POST" }, Route = "api/v3.0/p/sdk/sitemap/area/{areaId}/delete")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult DeleteSitemapArea(Guid areaId, [FromQuery]Guid? appId = null)
		{
			var response = new ResponseModel();
			response.Message = "Success";
			response.Success = true;

			if (areaId == Guid.Empty)
			{
				response.Message = "Area Id needs to be submitted";
				response.Success = false;
				return Json(response);
			}

			if (appId == null)
			{
				response.Message = "Application Id needs to be submitted as 'appId' query string";
				response.Success = false;
				return Json(response);
			}

			try
			{
				_appService.DeleteArea(areaId);
			}
			catch (Exception ex)
			{
				response.Message = ex.Message;
				response.Success = false;
				return Json(response);
			}

			var newSitemap = _appService.GetApplication(appId ?? Guid.Empty).Sitemap;
			var initData = new EntityRecord();
			initData["sitemap"] = _appService.OrderSitemap(newSitemap);
			initData["node_page_dict"] = _pageService.GetNodePageDictionary(appId);
			response.Object = initData;

			return Json(response);
		}
		#endregion

		#region << Sitemap Node >>
		[Authorize(Roles = "administrator")]
		[AcceptVerbs(new[] { "POST" }, Route = "api/v3.0/p/sdk/sitemap/node")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult CreateSitemapNode([FromBody]SitemapNodeSubmit node, [FromQuery]Guid? appId = null, [FromQuery]Guid? areaId = null)
		{
			var response = new ResponseModel();
			response.Message = "Success";
			response.Success = true;

			if (node == null)
			{
				response.Message = "Wrong object model submitted. Could not restore!";
				response.Success = false;
				return Json(response);
			}
			if (appId == null)
			{
				response.Message = "Application Id needs to be submitted as 'appId' query string";
				response.Success = false;
				return Json(response);
			}
			if (areaId == null)
			{
				response.Message = "Area Id needs to be submitted as 'areaId' query string";
				response.Success = false;
				return Json(response);
			}

			if (node.Id == Guid.Empty)
			{
				node.Id = Guid.NewGuid();
			}

			try
			{
				_appService.CreateAreaNode(node.Id, areaId ?? Guid.Empty, node.Name, node.Label,
					node.IconClass, node.Url, node.Type, node.EntityId, node.Weight, node.Access, node.ParentId);
				if (node.Pages == null)
				{
					node.Pages = new List<Guid>();
				}
				foreach (var pageId in node.Pages)
				{
					var page = _pageService.GetPage(pageId);
					if (page == null)
					{
						throw new Exception("Page not found");
					}
					page.NodeId = node.Id;
					page.AreaId = areaId;
					_pageService.UpdatePage(page);
				}
			}
			catch (Exception ex)
			{
				response.Message = ex.Message;
				response.Success = false;
				return Json(response);
			}

			var newSitemap = _appService.GetApplication(appId ?? Guid.Empty).Sitemap;
			var initData = new EntityRecord();
			initData["sitemap"] = _appService.OrderSitemap(newSitemap);
			initData["node_page_dict"] = _pageService.GetNodePageDictionary(appId);
			response.Object = initData;

			return Json(response);
		}

		[Authorize(Roles = "administrator")]
		[AcceptVerbs(new[] { "POST" }, Route = "api/v3.0/p/sdk/sitemap/node/{nodeId}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult UpdateSitemapNode([FromBody]SitemapNodeSubmit node, [FromQuery]Guid? appId = null, [FromQuery]Guid? areaId = null)
		{
			var response = new ResponseModel();
			response.Message = "Success";
			response.Success = true;

			if (node == null)
			{
				response.Message = "Wrong object model submitted. Could not restore!";
				response.Success = false;
				return Json(response);
			}
			if (appId == null)
			{
				response.Message = "Application Id needs to be submitted as 'appId' query string";
				response.Success = false;
				return Json(response);
			}

			if (areaId == null)
			{
				response.Message = "Area Id needs to be submitted as 'areaId' query string";
				response.Success = false;
				return Json(response);
			}

			if (node.Id == Guid.Empty)
			{
				response.Message = "Node Id needs to be submitted";
				response.Success = false;
				return Json(response);
			}

			try
			{
				_appService.UpdateAreaNode(node.Id, areaId ?? Guid.Empty, node.Name, node.Label,
					node.IconClass, node.Url, node.Type, node.EntityId, node.Weight, node.Access, node.ParentId);

				var allAppPages = _pageService.GetAppControlledPages(appId ?? Guid.Empty);

				var currentAttachedNodePages = allAppPages.FindAll(x => x.NodeId == node.Id).Select(x => x.Id).ToList();
				var currentAttachedPagesHashSet = new HashSet<Guid>();
				foreach (var pageId in currentAttachedNodePages)
				{
					currentAttachedPagesHashSet.Add(pageId);
				}

				//Process submitted page Ids
				if (node.Pages == null)
					node.Pages = new List<Guid>();
				foreach (var pageId in node.Pages)
				{
					var page = _pageService.GetPage(pageId);
					if (page == null)
					{
						throw new Exception("Page not found");
					}
					if (page.NodeId == null)
					{
						page.NodeId = node.Id;
						page.AreaId = areaId;
						_pageService.UpdatePage(page);
					}
					else if (page.NodeId == node.Id)
					{
						currentAttachedPagesHashSet.Remove(page.Id);
					}
				}

				//Detach pages that were not submitted
				foreach (var pageId in currentAttachedPagesHashSet)
				{
					var page = _pageService.GetPage(pageId);
					if (page == null)
					{
						throw new Exception("Page not found");
					}
					page.NodeId = null;
					page.AreaId = null;
					_pageService.UpdatePage(page);
				}

			}
			catch (Exception ex)
			{
				response.Message = ex.Message;
				response.Success = false;
				return Json(response);
			}

			var newSitemap = _appService.GetApplication(appId ?? Guid.Empty).Sitemap;
			var initData = new EntityRecord();
			initData["sitemap"] = _appService.OrderSitemap(newSitemap);
			initData["node_page_dict"] = _pageService.GetNodePageDictionary(appId);
			response.Object = initData;

			return Json(response);
		}

		[Authorize(Roles = "administrator")]
		[AcceptVerbs(new[] { "POST" }, Route = "api/v3.0/p/sdk/sitemap/node/{nodeId}/delete")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult DeleteSitemapNode(Guid nodeId, [FromQuery]Guid? appId = null)
		{
			var response = new ResponseModel();
			response.Message = "Success";
			response.Success = true;

			if (nodeId == Guid.Empty)
			{
				response.Message = "Node Id needs to be submitted";
				response.Success = false;
				return Json(response);
			}

			if (appId == null)
			{
				response.Message = "Application Id needs to be submitted as 'appId' query string";
				response.Success = false;
				return Json(response);
			}

			try
			{
				_appService.DeleteAreaNode(nodeId);
			}
			catch (Exception ex)
			{
				response.Message = ex.Message;
				response.Success = false;
				return Json(response);
			}

			var newSitemap = _appService.GetApplication(appId ?? Guid.Empty).Sitemap;
			var initData = new EntityRecord();
			initData["sitemap"] = _appService.OrderSitemap(newSitemap);
			initData["node_page_dict"] = _pageService.GetNodePageDictionary(appId);
			response.Object = initData;

			return Json(response);
		}

		//[AllowAnonymous] //Needed only when webcomponent development
		[Authorize(Roles = "administrator")]
		[AcceptVerbs(new[] { "GET" }, Route = "api/v3.0/p/sdk/sitemap/node/get-aux-info")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult GetNodeAuxData([FromQuery]Guid? appId = null)
		{
			var response = new ResponseModel();
			response.Message = "Success";
			response.Success = true;

			if (appId == null)
			{
				response.Message = "Application Id needs to be submitted as 'appId' query string";
				response.Success = false;
				return Json(response);
			}

			try
			{
				var responseObject = new EntityRecord();
				var entitiesSelectOptions = new List<SelectOption>();
				var appPageRecords = new List<EntityRecord>();
				var allEntityPageRecords = new List<EntityRecord>();
				var typesSelectOptions = new List<SelectOption>();
				var entities = _entityManager.ReadEntities().Object;
				foreach (var entity in entities)
				{
					var selectOption = new SelectOption()
					{
						Value = entity.Id.ToString(),
						Label = entity.Name
					};
					entitiesSelectOptions.Add(selectOption);
				}
				entitiesSelectOptions = entitiesSelectOptions.OrderBy(x => x.Label).ToList();

				entitiesSelectOptions.Insert(0, new SelectOption() { Value = "", Label = "not attached" });
				responseObject["all_entities"] = entitiesSelectOptions;



				foreach (var typeEnum in Enum.GetValues(typeof(SitemapNodeType)).Cast<SitemapNodeType>())
				{
					var selectOption = new SelectOption()
					{
						Value = ((int)typeEnum).ToString(),
						Label = GetEnumLabel(typeEnum)
					};
					typesSelectOptions.Add(selectOption);
				}
				responseObject["node_types"] = typesSelectOptions.OrderBy(x => x.Label).ToList();

				//Get App pages
				var appPages = _pageService.GetAppControlledPages(appId.Value);
				var allAppPagesWithoutNodes = appPages.FindAll(x => x.NodeId == null && x.Type == PageType.Application).OrderBy(x => x.Name).ToList();
				foreach (var page in allAppPagesWithoutNodes)
				{
					var selectOption = new EntityRecord();
					selectOption["page_id"] = page.Id.ToString();
					selectOption["page_name"] = page.Name;
					selectOption["node_id"] = page.NodeId != null ? (page.NodeId ?? Guid.Empty).ToString() : "";
					appPageRecords.Add(selectOption);
				}
				responseObject["app_pages"] = appPageRecords.OrderBy(x => (string)x["page_name"]).ToList();

				//Get EntityPages
				var allEntityPages = _pageService.GetAllPages();
				foreach (var page in allEntityPages)
				{
					if (page.EntityId != null && page.AppId == appId.Value)
					{
						var selectOption = new EntityRecord();
						selectOption["page_id"] = page.Id.ToString();
						selectOption["page_name"] = page.Name;
						selectOption["entity_id"] = page.EntityId;
						selectOption["type"] = ((int)page.Type).ToString();
						selectOption["node_id"] = page.NodeId != null ? (page.NodeId ?? Guid.Empty).ToString() : "";
						allEntityPageRecords.Add(selectOption);
					}
				}
				responseObject["all_entity_pages"] = allEntityPageRecords.OrderBy(x => (string)x["page_name"]).ToList();

				response.Object = responseObject;
			}
			catch (Exception ex)
			{
				response.Message = ex.Message;
				response.Success = false;
				return Json(response);
			}

			return Json(response);
		}


		#endregion

		#region << Private Helpers >>

		/// <summary>
		/// Retrieves the label from the <see cref="SelectOptionAttribute"/> applied to an enum value.
		/// This replaces the monolith's <c>WebVella.Erp.Web.Utils.ModelExtensions.GetLabel&lt;T&gt;</c>
		/// extension method, which is not available in the Admin microservice since it was defined
		/// in the Web layer. The logic is identical: reflect on the enum member's custom attributes
		/// and extract the Label property from the first <see cref="SelectOptionAttribute"/> found.
		/// </summary>
		/// <typeparam name="T">An enum type decorated with <see cref="SelectOptionAttribute"/>.</typeparam>
		/// <param name="e">The enum value to read the label from.</param>
		/// <returns>The label string from the attribute, or an empty string if none is found.</returns>
		private static string GetEnumLabel<T>(T e) where T : IConvertible
		{
			string label = "";

			if (e is Enum)
			{
				Type type = e.GetType();
				Array values = Enum.GetValues(type);

				foreach (int val in values)
				{
					if (val == e.ToInt32(CultureInfo.InvariantCulture))
					{
						var memInfo = type.GetMember(type.GetEnumName(val));
						var soAttributes = memInfo[0].GetCustomAttributes(typeof(SelectOptionAttribute), false);
						if (soAttributes.Length > 0)
						{
							label = ((SelectOptionAttribute)soAttributes[0]).Label;
						}

						break;
					}
				}
			}

			return label;
		}

		#endregion
	}
}
