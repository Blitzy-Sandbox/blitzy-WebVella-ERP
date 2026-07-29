<!--{"sort_order":10, "name": "wv-active-page-equals", "label": "wv-active-page-equals"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-active-page-equals

## Purpose

This tag helper sets an `active` class to the element, if the current `ViewContext.RouteData.Values["page"].ToString().ToLowerInvariant()`, regexed equals the string from `asp-page` attribute(trimmed, lowercased). If no `asp-page` attribute present, `href` is similarly checked.

## Properties

| name | description |
|------|------|
| `wv-active-page-equals` | *html target*: `attribute`<br>*object type*: `has no value`<br>*default value*: `none`<br>*is required*: `TRUE`<br>Just the attribute is required. It has no value needed. |
| `asp-page or href` | *html target*: `attribute`<br>*object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `TRUE`<br>This attribute is required to be present. If not <code>active</code> class will not be assigned. |

## Example

```html
<a wv-active-page-equals asp-page='/dev/base-plugin/api/index'>Api Index Page</a>
<a wv-active-page-equals href='/dev/base-plugin/api/index'>Api Index Page</a>
```

