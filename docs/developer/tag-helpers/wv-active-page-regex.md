<!--{"sort_order":10, "name": "wv-active-page-regex", "label": "wv-active-page-regex"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-active-page-regex

## Purpose

This tag helper sets an `active` class to the element, if the current page path (trimmed and lowercased) matches the provided regex pattern. If no active page present, `href` is similarly checked.

## Properties

+-----------------------------------+-----------------------------------+
| name                              | description                       |
+===================================+===================================+
|`wv-active-page-regex`             | *html target*: `attribute`        
|                                   |         
|                                   | *object type*: `Regex pattern`
|                                   |         
|                                   | *default value*: `none`                    
|                                   |
|                                   | *is required*: `TRUE`                      
|                                   |                                   
|                                   | A valid regex pattern to be matched.
+-----------------------------------+-----------------------------------+


## Example

```html
<a wv-active-page-regex='/dev/base-plugin/api/index'>Api Index Page</a>
```

