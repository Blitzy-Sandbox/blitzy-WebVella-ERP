<!--{"sort_order":10, "name": "wv-filter-autonumber", "label": "wv-filter-autonumber"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-filter-autonumber

## Purpose

`<wv-filter-autonumber/>`. Provides the ability to target a autonumber type field of the grid records, by applying the data type specifics.

## Properties
**Important**: All `<wv-filter-*>` helpers inherit a ["base tag helper" properties](wv-filter-base.md). In the following list are presented only the properties that this tag helper adds or alters. Not all base tag helper properties can be implemented by this tag helper too.

<table>
<thead><tr><th>name</th><th>description</th></tr></thead>
<tbody>
<tr><td colspan="2">does not have any specific properties</td></tr>
</tbody>
</table>

## Example

```html
<wv-filter-autonumber name="@name" label="@label" query-type="@queryType" query-options="@queryOptions"></wv-filter-autonumber>
```

