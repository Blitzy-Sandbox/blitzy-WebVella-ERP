<!--{"sort_order":10, "name": "wv-validation", "label": "wv-validation"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-validation

## Purpose

`<wv-validation/>`. Used when a validation message is needed outside of a the `<wv-form/>` element

## Properties

| name | description |
|------|------|
| `show-errors` | *object type*: `bool`<br>*default value*: `TRUE`<br>*is required*: `FALSE`<br>Whether to show a list of specific errors in the validation element |
| `validation` | *object type*: `ValidationException`<br>*default value*: `new ValidationException()`<br>*is required*: `FALSE`<br>The validation details to present |

## Example

```html
<wv-validation show-errors="false" validation="@validation"></wv-validation>
```

