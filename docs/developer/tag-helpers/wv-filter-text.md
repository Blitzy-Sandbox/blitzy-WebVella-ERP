<!--{"sort_order":10, "name": "wv-filter-text", "label": "wv-filter-text"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-filter-text

## Purpose

`<wv-filter-text/>`. Provides the ability to target a text type field of the grid records, by applying the data type specifics.

## Properties
**Important**: All `<wv-filter-*>` helpers inherit a ["base tag helper" properties](wv-filter-base.md). In the following list are presented only the properties that this tag helper adds or alters. Not all base tag helper properties can be implemented by this tag helper too.

+-------------------------------+-----------------------------------+
| name                          | description                       |
+===============================+===================================+
| does not have any specific properties                             | 
+-------------------------------------------------------------------+

## Example

```html
<wv-filter-text name="@name" label="@label" query-type="@queryType" query-options="@queryOptions"></wv-filter-text>
```

