<!--{"sort_order":10, "name": "wv-field-checkbox-grid", "label": "wv-field-checkbox-grid"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-field-checkbox-grid

## Purpose

`<wv-field-checkbox-grid/>`. Provides the ability to render grid like checkbox selections.


## Properties
**Important**: All `<wv-field-*>` helpers inherit a ["base tag helper" properties](wv-field-base.md). In the following list are presented only the properties that this tag helper adds or alters. Not all base tag helper properties can be implemented by this tag helper too.

+-------------------------------+-----------------------------------+
| name                          | description                       |
+===============================+===================================+
| `columns`                     | *object type*: `List<SelectOption>`                         
|                               |         
|                               | *default value*: `new List<SelectOption>()`
|                               |
|                               | *is required*: `FALSE`                      
|                               |                                   
|                               | The object is used for generating the grid's columns and determining the selections. The Value property is used in determination, The Label property is used as a column label.
+-------------------------------+-----------------------------------+
| `rows`                        | *object type*: `List<SelectOption>`                         
|                               |         
|                               | *default value*: `new List<SelectOption>()`
|                               |
|                               | *is required*: `FALSE`                      
|                               |                                   
|                               | The object is used for generating the grid's rows and determining the selections. The Value property is used in determination, The Label property is used as a row label.
+-------------------------------+-----------------------------------+
| `text-true`                   | *object type*: `string`                         
|                               |         
|                               | *default value*: `selected`
|                               |
|                               | *is required*: `FALSE`                      
|                               |                                   
|                               | The text presented as label for all checkboxes in the grid.
+-------------------------------+-----------------------------------+
| `value`                       | *object type*: `string`                         
|                               |         
|                               | *default value*: `string.Empty`
|                               |
|                               | *is required*: `FALSE`                      
|                               |                                   
|                               | the provided string/json value to the field will be deserialized to a `List<KeyStringList>`. This object is in general shows for a row value, which column values are selected.
+-------------------------------+-----------------------------------+


## Example

```html
@{
	var chkValues =  JsonConvert.Serialize(new List<KeyStringList>());
}
<wv-field-checkbox-grid value="@chkValues"></wv-field-checkbox>
```

