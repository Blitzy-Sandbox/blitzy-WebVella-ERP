<!--{"sort_order":10, "name": "wv-field-datetime", "label": "wv-field-datetime"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-field-datetime

## Purpose

`<wv-field-datetime/>`. Provides the ability to render the datetime field type of an Erp Entity. Can be used to render other datetime based form values.


## Properties
**Important**: All `<wv-field-*>` helpers inherit a ["base tag helper" properties](wv-field-base.md). In the following list are presented only the properties that this tag helper adds or alters. Not all base tag helper properties can be implemented by this tag helper too.

+-------------------------------+-----------------------------------+
| name                          | description                       |
+===============================+===================================+
| `value`                       | *object type*: `DateTime?`                         
|                               |         
|                               | *default value*: `NULL`
|                               |
|                               | *is required*: `FALSE`                      
|                               |                                   
|                               | All datetime's in the database are stored in UTC timezone and are expected to be submitted to the server either with a timezone or will be considered UTC
+-------------------------------+-----------------------------------+

## Example

```html
<wv-field-datetime value="@value"></wv-field-datetime>
```

