<!--{"sort_order":10, "name": "wv-grid", "label": "wv-grid"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-grid

## Purpose

`<wv-grid/>`. Generates a table / grid with integrated paging, sorting and more. The primary tool for presenting list of entity records. This tag helper is used in conjunction with `<wv-grid-row/>` and `<wv-grid-column/>`

## Properties

| name | description |
|------|------|
| `bordered` | *object type*: `bool`<br>*default value*: `FALSE`<br>*is required*: `FALSE`<br>Draws table borders, by applying Bootstrap styling |
| `borderless` | *object type*: `bool`<br>*default value*: `FALSE`<br>*is required*: `FALSE`<br>Draws borderless table, by applying Bootstrap styling |
| `class` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `FALSE`<br>Added to the grid generated classes |
| `columns` | *object type*: `List<GridColumn>`<br>*default value*: `new List<GridColumn>()`<br>*is required*: `FALSE`<br>Describes the columns of the grid. |
| `culture` | *object type*: `CultureInfo`<br>*default value*: `new CultureInfo("en-US")`<br>*is required*: `FALSE`<br>Used in data presentation. Could be inherited by other helpers wrapped in a <wv-grid/> |
| `has-tfoot` | *object type*: `bool`<br>*default value*: `TRUE`<br>*is required*: `FALSE`<br>If FALSE, it will hide the grid's tfoot element |
| `has-thead` | *object type*: `bool`<br>*default value*: `TRUE`<br>*is required*: `FALSE`<br>If FALSE, it will hide the grid's thead element |
| `hover` | *object type*: `bool`<br>*default value*: `FALSE`<br>*is required*: `FALSE`<br>Changes the background of the hovered table row, by applying Bootstrap styling |
| `id` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `FALSE`<br>Html ID you may need to set to the rendered element |
| `name` | *object type*: `string`<br>*default value*: `string.Empty`<br>*is required*: `FALSE`<br>Used in grid's CSS class generation |
| `page` | *object type*: `int`<br>*default value*: `0`<br>*is required*: `FALSE`<br>Sets the current page value |
| `page-size` | *object type*: `int`<br>*default value*: `0`<br>*is required*: `FALSE`<br>Sets the expected page size |
| `prefix` | *object type*: `string`<br>*default value*: `string.Empty`<br>*is required*: `FALSE`<br>If you have two or more grids on a single page, each grid could apply a prefix to it query parameters it applies. |
| `query-string-page` | *object type*: `string`<br>*default value*: `page`<br>*is required*: `FALSE`<br>This will override the default query string name for pagination. Used only in grid links generation |
| `query-string-sortby` | *object type*: `string`<br>*default value*: `sortBy`<br>*is required*: `FALSE`<br>This will override the default query string name for sorting. Used only in grid links generation |
| `query-string-sort-order` | *object type*: `string`<br>*default value*: `sortOrder`<br>*is required*: `FALSE`<br>This will override the default query string name for sorting order. Used only in grid links generation |
| `responsive-breakpoint` | *object type*: `CssBreakpoint`<br>*default value*: `CssBreakpoint.None`<br>*is required*: `FALSE`<br>Across every breakpoint for horizontally scrolling tables. |
| `small` | *object type*: `bool`<br>*default value*: `FALSE`<br>*is required*: `FALSE`<br>less padding of the table cells, by applying Bootstrap styling |
| `striped` | *object type*: `bool`<br>*default value*: `FALSE`<br>*is required*: `FALSE`<br>Alternates the background of each row of the table, by applying Bootstrap styling |
| `total-count` | *object type*: `int`<br>*default value*: `0`<br>*is required*: `FALSE`<br>Sets the total count of the records |
| `vertical-align` | *object type*: `VerticalAlignmentType`<br>*default value*: `VerticalAlignmentType.None`<br>*is required*: `FALSE`<br>vertical alignment with the table. Options are: None, Top, Middle, Bottom |


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

