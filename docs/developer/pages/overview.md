<!--{"sort_order":1, "name": "overview", "label": "Overview"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# Overview

The pages are the main tool for presenting content in the system. The easiest way to create, review or manage pages is through the SDK Plugin interface. 
A page can be used by more then one application. In this case, the page could be opened from more then one url and this is OK.

## Page types

There are several page types, that have different purpose withing the built in logic of the WebVella ERP. They are: home page (only one), site pages, application pages, erp record details, erp record create, erp record details, erp record delete.

Based on the page type, the system will automatically fill in the corresponding properties in the Page DataModel, which is one of the main purposes of the page types.

## Page properties

| name | description |
|------|------|
| `AppId` | *object type*: `Guid?`<br>*default value*: `NULL`<br>*is required*: `FALSE`<br>Required for Application type pages. Sets the attached application |
| `AreaId` | *object type*: `Guid?`<br>*default value*: `NULL`<br>*is required*: `FALSE`<br>When a page is attached to an application sitemap, this is set to the sitemap area id |
| `EntityId` | *object type*: `Guid?`<br>*default value*: `NULL`<br>*is required*: `FALSE`<br>Required for Entity related page types. Sets the attached entity |
| `IconClass` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `FALSE`<br>Used for visual representation of the page |
| `Id` | *object type*: `Guid`<br>*default value*: `Guid.Empty`<br>*is required*: `TRUE`<br>Unique identifier for the page |
| `IsRazorBody` | *object type*: `bool`<br>*default value*: `FALSE`<br>*is required*: `TRUE`<br>Show whether it should render its custom code body rather then the automatically generated one |
| `Label` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `TRUE`<br>Used as a page title and in lists |
| `LabelTranslations` | *object type*: `List<TranslationResource>`<br>*default value*: `new List<TranslationResource>()`<br>*is required*: `FALSE`<br>Ability to provide translations for the page title |
| `Layout` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `TRUE`<br>The Master page that should be applied as a layout for the page generated body |
| `Name` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `TRUE`<br>Used as a part of the page URL |
| `NodeId` | *object type*: `Guid?`<br>*default value*: `NULL`<br>*is required*: `FALSE`<br>When a page is attached to an application sitemap, this is set to the sitemap node id |
| `RazorBody` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `TRUE`<br>The code of the Custom page body |
| `System` | *object type*: `bool`<br>*default value*: `FALSE`<br>*is required*: `TRUE`<br>When creating an application, you will need to define a schema that you need to be managed only by the corresponding plugin and not the administrator. By marking the page as System, the interface will lock certain changes. |
| `Type` | *object type*: `PageType`<br>*default value*: `string.empty`<br>*is required*: `TRUE`<br>Sets the page type. The Options are: Home, Site, Application, RecordList, RecordCreate, RecordDetails, RecordManage |
| `Weight` | *object type*: `int`<br>*default value*: `10`<br>*is required*: `TRUE`<br>Used to sort pages when needed |
