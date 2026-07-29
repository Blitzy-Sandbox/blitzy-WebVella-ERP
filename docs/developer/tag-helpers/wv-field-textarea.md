<!--{"sort_order":10, "name": "wv-field-textarea", "label": "wv-field-textarea"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-field-textarea

## Purpose

`<wv-field-textarea/>`. Provides the ability to render the multiline-text field type of an Erp Entity. Can be used to render other longer text based form values.


## Properties
**Important**: All `<wv-field-*>` helpers inherit a ["base tag helper" properties](wv-field-base.md). In the following list are presented only the properties that this tag helper adds or alters. Not all base tag helper properties can be implemented by this tag helper too.

| name | description |
|------|------|
| `autogrow` | *object type*: `bool`<br>*default value*: `FALSE`<br>*is required*: `FALSE`<br>will increase the textarea height automatically based on the text content |
| `height` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `FALSE`<br>will be set as a height style to the textarea input |

## Example

```html
<wv-field-textarea value="@value"></wv-field-textarea>
```

