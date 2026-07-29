<!--{"sort_order":10, "name": "wv-row", "label": "wv-row"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-row

## Purpose

`<wv-row/>`. Helps in rendering a Bootstrap Layout Grid column and its options.

## Properties

| name | description |
|------|------|
| `class` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Additional CSS classes to be added to the element |
| `flex-horizontal-alignment` | *object type*: `FlexHorizontalAlignmentType`<br>*default value*: `FlexHorizontalAlignmentType.None`<br>*is required*: `FALSE`<br>Sets the horizontal positioning of the nested columns as per flex standard. Options are: None, Start, Center, End, Around, Between |
| `flex-vertical-alignment` | *object type*: `FlexVerticalAlignmentType`<br>*default value*: `FlexVerticalAlignmentType.None`<br>*is required*: `FALSE`<br>Sets the vertical positioning of the nested columns as per flex standard. Options are: None, Start, Center, End |
| `no-gutters` | *object type*: `bool`<br>*default value*: `FALSE`<br>*is required*: `FALSE`<br>Removes the nested column padding as per Bootstrap styling |


## Example

```html
<wv-row>
	<wv-column span="6">...<wv-column>
	<wv-column span="6">...<wv-column>
</wv-row>
```

