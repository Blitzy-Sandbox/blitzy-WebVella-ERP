<!--{"sort_order":10, "name": "wv-grid-column", "label": "wv-grid-column"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-grid-column

## Purpose

`<wv-grid-column/>`. This tag helper is used in conjunction with `<wv-grid/>` and `<wv-grid-row/>` to generate a grid.

## Properties

| name | description |
|------|------|
| `class` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `FALSE`<br>Added to the grid generated classes |
| `horizontal-align` | *object type*: `HorizontalAlignmentType`<br>*default value*: `HorizontalAlignmentType.None`<br>*is required*: `FALSE`<br>horizontal alignment with this column's table cells. Options are: None, Left, Center, Right |
| `text-wrap` | *object type*: `bool`<br>*default value*: `TRUE`<br>*is required*: `FALSE`<br>enable or disable the text-wrapping in this column's table cells |
| `vertical-align` | *object type*: `VerticalAlignmentType`<br>*default value*: `VerticalAlignmentType.None`<br>*is required*: `FALSE`<br>vertical alignment with this column's table cells. Options are: None, Top, Middle, Bottom |


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

