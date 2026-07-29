<!--{"sort_order":10, "name": "wv-form", "label": "wv-form"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-form

## Purpose

`<wv-form/>`. Presents a form with an ability to autogenerate a proper antiforgery token.

## Properties

| name | description |
|------|------|
| `accept-charset` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Html `accept-charset` attribute you may need to set to the rendered element |
| `action` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Html `action` attribute you may need to set to the rendered element |
| `antiforgery` | *object type*: `bool`<br>*default value*: `true`<br>*is required*: `FALSE`<br>If true adds a antiforgery hidden input with its proper value so this form can be submitted towards a Razor Page |
| `autocomplete` | *object type*: `bool`<br>*default value*: `TRUE`<br>*is required*: `FALSE`<br>Html `autocomplete` attribute you may need to set to the rendered element. |
| `enctype` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Html `enctype` attribute you may need to set to the rendered element. Specifies how the form-data should be encoded when submitting it to the server (only for method="post"). Options are: application/x-www-form-urlencoded, multipart/form-data, text/plain |
| `id` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Html ID you may need to set to the rendered element |
| `label-mode` | *object type*: `LabelRenderMode`<br>*default value*: `LabelRenderMode.Undefined`<br>*is required*: `FALSE`<br>How the labels of any wrapped fields should be presented. Useful in order to set this option only on when place. Inherited by the wrapped fields. |
| `method` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Html `method` attribute you may need to set to the rendered element. Options are: get or post |
| `mode` | *object type*: `FieldRenderMode`<br>*default value*: `FieldRenderMode.Undefined`<br>*is required*: `FALSE`<br>How any wrapped fields should be presented. Useful in order to set this option only on when place. Inherited by the wrapped fields. |
| `name` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Html `name` attribute you may need to set to the rendered element |
| `novalidate` | *object type*: `bool`<br>*default value*: `TRUE`<br>*is required*: `FALSE`<br>Html `novalidate` attribute you may need to set to the rendered element. |
| `target` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Html `target` attribute you may need to set to the rendered element. Specifies where to display the response that is received after submitting the form. Options are: _blank, _self, _parent, _top |
| `validation` | *object type*: `ValidationException`<br>*default value*: `NULL`<br>*is required*: `FALSE`<br>Helps to present any validation messages within the form to the end user |

## Example

```html
<wv-form name="ManageRecord" validation="Model.Validation" label-mode="Stacked" mode="Form" autocomplete="false">
	<wv-row>
		<wv-column span="6">
			<wv-field-checkbox label-text="Enabled" value="@Model.Enabled" name="Enabled" text-true="enable this schedule plan"></wv-field-checkbox>
		</wv-column>
		<wv-column span="6">
			<wv-field-guid label-text="Id" value="@Model.Id" access="ReadOnly" name="Id"></wv-field-guid>
		</wv-column>
	</wv-row>
</wv-form>
```

