<!--{"sort_order":10, "name": "wv-field-email", "label": "wv-field-email"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-field-email

## Purpose

`<wv-field-email/>`. Provides the ability to render the email field type of an Erp Entity. Can be used to render other email based form values.


## Properties
**Important**: All `<wv-field-*>` helpers inherit a ["base tag helper" properties](wv-field-base.md). In the following list are presented only the properties that this tag helper adds or alters. Not all base tag helper properties can be implemented by this tag helper too.

+-------------------------------+-----------------------------------+
| name                          | description                       |
+===============================+===================================+
| `maxlength`                   | *object type*: `int?`                         
|                               |         
|                               | *default value*: `NULL`
|                               |
|                               | *is required*: `FALSE`                      
|                               |                                   
|                               | If present, will be set as a maxlength attribute of the field's input
+-------------------------------+-----------------------------------+

## Example

```html
<wv-field-email value="@value"></wv-field-email>
```

