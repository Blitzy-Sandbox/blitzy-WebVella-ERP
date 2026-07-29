<!--{"sort_order":10, "name": "wv-field-text", "label": "wv-field-text"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-field-text

## Purpose

`<wv-field-text/>`. Provides the ability to render the text field type of an Erp Entity. Can be used to render other text based form values.


## Properties
**Important**: All `<wv-field-*>` helpers inherit a ["base tag helper" properties](wv-field-base.md). In the following list are presented only the properties that this tag helper adds or alters. Not all base tag helper properties can be implemented by this tag helper too.

| name | description |
|------|------|
| `maxlength` | *object type*: `int?`<br>*default value*: `NULL`<br>*is required*: `FALSE`<br>If present, will be set as a maxlength attribute of the field's input |

## Example

```html
<wv-field-text value="@value"></wv-field-text>
```

