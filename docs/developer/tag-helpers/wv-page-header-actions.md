<!--{"sort_order":10, "name": "wv-page-header-actions", "label": "wv-page-actions"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-page-header-actions

## Purpose

`<wv-page-header-actions/>`. Presents the page header action buttons on the right-hand side of the page header.  Optional in generating the standard page header element. Used in conjunction with `<wv-page-header/>`.

## Properties

<table>
<thead><tr><th>name</th><th>description</th></tr></thead>
<tbody>
<tr><td colspan="2">does not have any specific properties</td></tr>
</tbody>
</table>


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

