<!--{"sort_order":10, "name": "wv-field-checkbox", "label": "wv-field-checkbox"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-field-checkbox

## Purpose

`<wv-field-checkbox/>`. Provides the ability to render the checkbox field type of an Erp Entity. Can be used to render other boolean based form values.


## Properties
**Important**: All `<wv-field-*>` helpers inherit a ["base tag helper" properties](wv-field-base.md). In the following list are presented only the properties that this tag helper adds or alters. Not all base tag helper properties can be implemented by this tag helper too.

| name | description |
|------|------|
| `text-true` | *object type*: `string`<br>*default value*: `selected`<br>*is required*: `FALSE`<br>The text presented as checkbox label in forms and also as checkbox value in Simple mode, when checked. |
| `text-false` | *object type*: `string`<br>*default value*: `not selected`<br>*is required*: `FALSE`<br>The text presented as checkbox value in Simple mode, when checked. |

## Example

```html
<wv-field-checkbox value="@value" text-true="is for sale"></wv-field-checkbox>
```

