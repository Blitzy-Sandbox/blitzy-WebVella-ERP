<!--{"sort_order":10, "name": "wv-authorize", "label": "wv-authorize"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-authorize

## Purpose

Attribute only tag helper. Will render the attributed element and its contents, only if the authorize check is successful. The `wv-authorize` is used alone 
or in conjunction with `erp-allow-roles` and `erp-block-roles`. 

## Properties

| name | description |
|------|------|
| `wv-authorize` | *html target*: `attribute`<br>*object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `TRUE`<br>A role name or a csv list of role names, which the user needs to have in order to access the element. |
| `erp-allow-roles` | *html target*: `attribute`<br>*object type*: `string or CSV`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Used as HTML Attribute to enabled the authorizations |
| `erp-block-roles` | *html target*: `attribute`<br>*object type*: `string or CSV`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>A role name or a csv list of role names, which the user should not have in order to access the element. With priority over the previous attribute `erp-allow-roles` |

## Example

| goal | code |
|------|------|
| User is authenticated | `<div wv-authorize>...</div>` |
| User is guest | `<div wv-authorize erp-allow-roles="guest">...</div>` |
| User doesn't have `administrator` role | `<div wv-authorize erp-block-roles="administrator">...</div>` |
| User is not `guest` | `<div wv-authorize erp-block-roles="guest">...</div>` |
| User has either `administrator` or `regular` role | `<div wv-authorize erp-allow-roles="guest,regular">...</div>` |
| User has `administrator` but not `manager` role | `<div wv-authorize erp-allow-roles="administrator" erp-block-roles="manager">...</div>` |

```html
<div wv-authorize erp-allow-roles="administrator,manager" erp-block-roles="sales"></div>
```

