<!--{"sort_order":10, "name": "wv-section", "label": "wv-section"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-section

## Purpose

`<wv-section/>`. Groups fields in a section or a card, with the integrated option to collapse

## Properties

| name | description |
|------|------|
| `body-class` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Additional CSS classes to be added to the body of the element |
| `class` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Additional CSS classes to be added to the element |
| `field-mode` | *object type*: `FieldRenderMode`<br>*default value*: `FieldRenderMode.Undefined`<br>*is required*: `FALSE`<br>Does not have effect on the section element, but on the nested fields as it could be inherited by default. Options are: Undefined, Form, Display, InlineEdit, Simple |
| `id` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>HTML Id of the generated element |
| `is-card` | *object type*: `bool`<br>*default value*: `FALSE`<br>*is required*: `FALSE`<br>Whether to render the element as a card |
| `is-collapsable` | *object type*: `bool`<br>*default value*: `FALSE`<br>*is required*: `FALSE`<br>Add the option of the element's body to be collapsed by clicking on its title |
| `is-collapsed` | *object type*: `bool`<br>*default value*: `TRUE`<br>*is required*: `FALSE`<br>Sets the initial collapse status |
| `label-mode` | *object type*: `LabelRenderMode`<br>*default value*: `LabelRenderMode.Undefined`<br>*is required*: `FALSE`<br>Does not have effect on the section element, but on the nested fields as it could be inherited by default. Options: Undefined, Stacked, Horizontal, Hidden |
| `title` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>The section title |
| `title-tag` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>The HTML tag for wrapping the title text |



## Example

```html
<wv-section class="mt-4" label-mode="@LabelRenderMode.Hidden">
	<wv-row>
		<wv-column span="6">
			<wv-field-text label-text="Name" value="@Model.Name" name="Name" required="true"></wv-field-text>
		</wv-column>
		<wv-column span="6">
			<wv-field-text label-text="Label" value="@Model.Label" name="Label" required="true"></wv-field-text>
		</wv-column>
	</wv-row>
</wv-section>
```

