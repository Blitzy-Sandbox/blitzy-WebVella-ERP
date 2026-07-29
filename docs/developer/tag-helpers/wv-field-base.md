<!--{"sort_order":10, "name": "wv-field-base", "label": "wv-field-*"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-field-* base properties

## Purpose

All field tag helpers inherit this base field's properties. Some of them can be overrided or not used by the specific field though. Check the relevant tag helper document page for more information.

## Properties

| name | description |
|------|------|
| `access` | *object type*: `FieldAccess`<br>*default value*: `FieldAccess.Full`<br>*is required*: `FALSE`<br>Sets whether the user can interact or view that value of the field. Options are: Undefined, Full, FullAndCreate, ReadOnly, Forbidden |
| `access-denied-message` | *object type*: `string`<br>*default value*: `access denied`<br>*is required*: `FALSE`<br>Overrides the default access denied message, presented to the user when he/she doesn't have access to the field value |
| `api-url` | *object type*: `string`<br>*default value*: `string.Empty`<br>*is required*: `FALSE`<br>Overrides the default API URL call, when InlineEdit a field |
| `autocomplete` | *object type*: `bool`<br>*default value*: `NULL`<br>*is required*: `FALSE`<br>Ability to set the autocomplete attribute of the field's input, used by the browsers to prefill form data. |
| `class` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>A CSS class to be added to the general classes of the field. |
| `field-id` | *object type*: `Guid?`<br>*default value*: `NULL`<br>*is required*: `FALSE`<br>Field Id is used when initializing scripts |
| `description` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Presents a description text after the field's input |
| `default-value` | *object type*: `dynamic`<br>*default value*: `NULL`<br>*is required*: `FALSE`<br>Depends on the field type. |
| `empty-value-message` | *object type*: `string`<br>*default value*: `no data`<br>*is required*: `FALSE`<br>Overrides the default message, when the field value is null |
| `entity-name` | *object type*: `string`<br>*default value*: `string.Empty`<br>*is required*: `FALSE`<br>Used in InlineEdit mode in order to set the entity of the altered record Id |
| `id` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Html ID you may need to set to the rendered field |
| `init-errors` | *object type*: `List<string>`<br>*default value*: `new List<string>()`<br>*is required*: `FALSE`<br>If any init errors are set, the field will render the label and an error message. Usually used for showing non validation system errors |
| `label-error-text` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Will render an error icon next to the label text, with the provided text as a tooltip |
| `label-help-text` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Will render a help icon next to the label text, with the provided text as a tooltip |
| `label-mode` | *object type*: `LabelRenderMode`<br>*default value*: `LabelRenderMode.Undefined`<br>*is required*: `FALSE`<br>Sets the rendering mode of the label. Options: Undefined, Stacked, Horizontal, Hidden |
| `label-text` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>The text rendered as a field label |
| `label-warning-text` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Will render a warning icon next to the label text, with the provided text as a tooltip |
| `locale` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>If set, this will initialize the `new CultureInfo(Locale)` object which will be used in the fields value presentation and localization |
| `mode` | *object type*: `FieldRenderMode`<br>*default value*: `FieldRenderMode.Undefined`<br>*is required*: `FALSE`<br>Defines how the field will be rendered. Options are: Undefined, Form, Display, InlineEdit, Simple |
| `name` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Will set the name attribute of the html element |
| `placeholder` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Sets the placeholder attribute of the field's input |
| `record-id` | *object type*: `Guid?`<br>*default value*: `NULL`<br>*is required*: `FALSE`<br>Used in InlineEdit mode and send to the API handler which looks for the field name as record property and alters it |
| `required` | *object type*: `bool`<br>*default value*: `FALSE`<br>*is required*: `FALSE`<br>Present a red asterix sign before the field's label |
| `validation-errors` | *object type*: `List<ValidationError>`<br>*default value*: `new List<ValidationError>()`<br>*is required*: `FALSE`<br>If any validation errors are set, the field will render them at its bottom. Used for form validation messages towards the end user. |
| `value` | *object type*: `dynamic`<br>*default value*: `NULL`<br>*is required*: `FALSE`<br>Depends on the field type. |

## Example

```html
<wv-field-autonumber id="my-drawer" title="This is a drawer">...</wv-field-autonumber>
```

