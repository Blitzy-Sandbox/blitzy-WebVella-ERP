<!--{"sort_order":10, "name": "wv-page-header", "label": "wv-page-header"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-page-header

## Purpose

`<wv-page-header/>`. Generates the standard page header element. Used in conjunction with `<wv-page-header-actions/>` and `<wv-page-header-toolbar/>`

## Properties

| name | description |
|------|------|
| `area-label` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `FALSE`<br>Area label string |
| `area-sublabel` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `FALSE`<br>Area sublabel string |
| `color` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `FALSE`<br>Color code that should be used in page header's area text |
| `description` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `FALSE`<br>Page description |
| `icon-class` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `FALSE`<br>Icon class with which to generate the page icon using [FontAwesome icon library](https://fontawesome.com/icons) |
| `icon-color` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `FALSE`<br>Color code that should be used in page header's icon |
| `page-switch-items` | *object type*: `List<PageSwitchItem>`<br>*default value*: `new List<PageSwitchItem>()`<br>*is required*: `FALSE`<br>Page switch dropdown, meant to present other pages on the same level or different list views. |
| `return-url` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `FALSE`<br>If set will present a "back button" in the left side of the element |
| `subtitle` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `FALSE`<br>Page subtitle |
| `title` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `FALSE`<br>Page title |


## Example

```html
<wv-page-header color="@color" icon-color="@iconColor" area-label="@areaLabel" area-sublabel="@areaSubLabel" title="@title"
	subtitle="@subTitle" description="@description" icon-class="@iconClass" return-url="@returnUrl" page-switch-items="@pageSwitchItems">
	<wv-page-header-actions>
		...
	</wv-page-header-actions>
	<wv-page-header-toolbar>
		...
	</wv-page-header-toolbar>
</wv-page-header>
```

