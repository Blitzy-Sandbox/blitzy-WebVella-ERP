<!--{"sort_order":10, "name": "wv-field-code", "label": "wv-field-code"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-field-code

## Purpose

`<wv-field-code/>`. Renders a specialized editor - [ace editor](https://ace.c9.io/) in order to make code submission much easier.


## Properties
**Important**: All `<wv-field-*>` helpers inherit a ["base tag helper" properties](wv-field-base.md). In the following list are presented only the properties that this tag helper adds or alters. Not all base tag helper properties can be implemented by this tag helper too.

+-------------------------------+-----------------------------------+
| name                          | description                       |
+===============================+===================================+
| `height`                      | *object type*: `string`                         
|                               |         
|                               | *default value*: `160px`
|                               |
|                               | *is required*: `FALSE`                      
|                               |                                   
|                               | Will be set as height style to the editor wrapper
+-------------------------------+-----------------------------------+
| `language`                    | *object type*: `string`                         
|                               |         
|                               | *default value*: `razor`
|                               |
|                               | *is required*: `FALSE`                      
|                               |                                   
|                               | Code highlighting is based on the language selection. Almost all modern languages are supported. Check the [editor's kitchen sink](https://ace.c9.io/build/kitchen-sink.html) for reference
+-------------------------------+-----------------------------------+
| `theme`                       | *object type*: `string`                         
|                               |         
|                               | *default value*: `razor`
|                               |
|                               | *is required*: `FALSE`                      
|                               |                                   
|                               | Code highlighting theme. Check the [editor's kitchen sink](https://ace.c9.io/build/kitchen-sink.html) for reference
+-------------------------------+-----------------------------------+

## Example

```html
<wv-field-code value="@codeSnippet" language="css"></wv-field-code>
```

