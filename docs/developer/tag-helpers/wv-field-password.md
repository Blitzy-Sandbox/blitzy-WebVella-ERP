<!--{"sort_order":10, "name": "wv-field-password", "label": "wv-field-password"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-field-password

## Purpose

`<wv-field-password/>`. Provides the ability to render the password field type of an Erp Entity.

## Properties
**Important**: All `<wv-field-*>` helpers inherit a ["base tag helper" properties](wv-field-base.md). In the following list are presented only the properties that this tag helper adds or alters. Not all base tag helper properties can be implemented by this tag helper too.

| name | description |
|------|------|
| `max` | *object type*: `decimal?`<br>*default value*: `NULL`<br>*is required*: `FALSE`<br>If present, will be set as a max attribute of the field's input |
| `min` | *object type*: `decimal?`<br>*default value*: `NULL`<br>*is required*: `FALSE`<br>If present, will be set as a min attribute of the field's input |
| `value` | *object type*: `string`<br>*default value*: `null`<br>*is required*: `FALSE`<br>The string value will never be presented or submitted to the server, event though the passwords are always stored encrypted within the database. It will present six star symbols instead when needed. |

## Example

```html
<wv-field-password value="@value"></wv-field-password>
```

