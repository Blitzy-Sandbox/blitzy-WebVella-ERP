<!--{"sort_order":10, "name": "wv-field-datasource", "label": "wv-field-datasource"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-field-datasource

## Purpose

`<wv-field-datasource/>`. This is a specific tag helper, that provides a way to easily submit JSON schemas for datasource or code based value calculation. Used mostly in the PageCompoment's Options forms.


## Properties
**Important**: All `<wv-field-*>` helpers inherit a ["base tag helper" properties](wv-field-base.md). In the following list are presented only the properties that this tag helper adds or alters. Not all base tag helper properties can be implemented by this tag helper too.

| name | description |
|------|------|
| `page-id` | *object type*: `Guid?`<br>*default value*: `NULL`<br>*is required*: `FALSE`<br>The ID of the page from which module the data will be extracted/calculated |
| `value` | *object type*: `string`<br>*default value*: `emtpy.string`<br>*is required*: `FALSE`<br>A JSON string value is expected, which will be parsed / deserialized to `DataSourceVariable` model |


## Example

```html
<wv-field-datasource value="@value" page-id="@pageId"></wv-field-datasource>
```

