<!--{"sort_order":7, "name": "record-list", "label": "Record list"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# Record list

This is the default presented page type, when an entity is connected to an application sitemap. Its url reflects that: `/{AppName}/{AreaName}/{NodeName}/l/{PageName?}`. If a `PageName` is not provided, the system will automatically open the record list page that is connected to the selected entity by the node and has the lowest page sort order.

There is one variation of this page, when you need to review a list of records as related to another parent record, from the same or another entity. We call this page "record related record list" and it is accessible on an url like: `/{AppName}/{AreaName}/{NodeName}/r/{RecordId}/rl/{RelationId}/l/{PageName?}`