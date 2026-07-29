<!--{"sort_order":10, "name": "wv-field-url", "label": "wv-field-url"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-field-url

## Purpose

`<wv-field-url/>`. Provides the ability to render the url field type of an Erp Entity. Can be used to render other url based form values.


## Properties
**Important**: All `<wv-field-*>` helpers inherit a ["base tag helper" properties](wv-field-base.md). In the following list are presented only the properties that this tag helper adds or alters. Not all base tag helper properties can be implemented by this tag helper too.

| name | description |
|------|------|
| `target-blank` | *object type*: `bool`<br>*default value*: `FALSE`<br>*is required*: `FALSE`<br>If TRUE, will open the corresponding link in a new browser tab |

## Example

```html
<wv-field-url value="@value"></wv-field-url>
```

