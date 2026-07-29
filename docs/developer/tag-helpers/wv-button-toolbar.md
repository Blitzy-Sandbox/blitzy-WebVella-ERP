<!--{"sort_order":10, "name": "wv-button-toolbar", "label": "wv-button-toolbar"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-button-toolbar

## Purpose

`<wv-button-toolbar/>`. Used to wrap multiple `<wv-button-group/>` and render them horizontal or vertical.

## Properties

| name | description |
|------|------|
| `class` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>CSS classes that you may need to add to the standard Bootstrap CSS |
| `id` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Html ID you may need to set to the rendered element |
| `is-vertical` | *object type*: `bool`<br>*default value*: `FALSE`<br>*is required*: `FALSE`<br>If TRUE, will render the button list vertically. |
| `size` | *object type*: `enum CssSize`<br>*default value*: `CssSize.Inherit`<br>*is required*: `FALSE`<br>Size of the element. Options are: Normal,Small,Large, Inherit |

## Example

```html
<wv-button-toolbar  size="@CssSize.Small">
	<wv-button-group>
		<wv-button text="Prev"></wv-button>
	</wv-button-group>
	<wv-button-group>
		<wv-button text="1"></wv-button>
		<wv-button text="2"></wv-button>
		<wv-button text="3"></wv-button>
	</wv-button-group>
	<wv-button-group>
		<wv-button text="Next"></wv-button>
	</wv-button-group>
</wv-button-toolbar>
```

