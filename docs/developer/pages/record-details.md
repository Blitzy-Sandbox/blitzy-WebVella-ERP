<!--{"sort_order":8, "name": "record-details", "label": "Record details"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# Record details

This page type is used for record details pages. Its url is like `/{AppName}/{AreaName}/{NodeName}/r/{RecordId}/{PageName?}`. If a `PageName` is not provided, the system will automatically open the record details page that is connected to the selected entity by the node and has the lowest page sort order.

There is one variation of this page, when you need to review the record as related to another parent record, from the same or another entity. We call this page "record related record details" and it is accessible on an url like: `/{AppName}/{AreaName}/{NodeName}/r/{RecordId}/rl/{RelationId}/r/{RelatedRecordId}/{PageName?}`