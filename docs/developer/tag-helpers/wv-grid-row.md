<!--{"sort_order":10, "name": "wv-grid-row", "label": "wv-grid-row"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-grid-row

## Purpose

`<wv-grid-row/>`. This tag helper is used in conjunction with `<wv-grid/>` and `<wv-grid-column/>` to generate a grid.

## Properties

<table>
<thead><tr><th>name</th><th>description</th></tr></thead>
<tbody>
<tr><td colspan="2">does not have any specific properties</td></tr>
</tbody>
</table>


## Example

```html
<wv-grid page="@pager" total-count="@totalCount" columns="@columns">
	@foreach(var record in records)
	{
		<wv-grid-row>
			<wv-grid-column>...</wv-grid-column>
			<wv-grid-column>...</wv-grid-column>
		</wv-grid-row>
	}
</wv-grid>
```

