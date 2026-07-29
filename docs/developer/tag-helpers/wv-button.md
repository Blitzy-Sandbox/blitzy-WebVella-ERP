<!--{"sort_order":10, "name": "wv-button", "label": "wv-button"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-button

## Purpose

`<wv-button/>`. Used to render button and links styled as buttons with added features for styling, sizing, form submission and click behavior. 

## Properties

| name | description |
|------|------|
| `class` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>CSS classes that you may need to add to the standard Bootstrap CSS |
| `color` | *object type*: `enum ErpColor`<br>*default value*: `ErpColor.White`<br>*is required*: `FALSE`<br>Select from 32 color options |
| `form` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Awaits a form HTML ID, which will be submitted when the button is pressed. Available only for `ButtonType.Submit` |
| `formaction` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>If submitted, will add a `formaction` attribute with this value. Available only for `ButtonType.Submit` |
| `href` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Used when the tag renders an HTML link element only. |
| `icon-class` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>If submitted an additional `<i/>` HTML element will be rendered before the button text with the set CSS class using [FontAwesome icon library](https://fontawesome.com/icons) |
| `id` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Html ID you may need to set to the rendered element |
| `is-active` | *object type*: `bool`<br>*default value*: `FALSE`<br>*is required*: `FALSE`<br>Additional `active` class will be added to the button |
| `is-block` | *object type*: `bool`<br>*default value*: `FALSE`<br>*is required*: `FALSE`<br>The button width will expand based on the container it is wrapped in |
| `is-disabled` | *object type*: `bool`<br>*default value*: `FALSE`<br>*is required*: `FALSE`<br>If the tag renders an HTML button, an additional `disabled` attribute will be added. If the tag renders a link, additional `disabled` class will be addded. |
| `is-outline` | *object type*: `bool`<br>*default value*: `FALSE`<br>*is required*: `FALSE`<br>Render only the button outlines and no background under the text |
| `new-tab` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Used when the tag renders an HTML link element only. Will add an attribute `target="_blank"` |
| `onclick` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>If submitted, an `onclick` attribute will be added to the element. |
| `size` | *object type*: `enum CssSize`<br>*default value*: `CssSize.Inherit`<br>*is required*: `FALSE`<br>Size of the element. Options are: Normal,Small,Large, Inherit |
| `text` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>The button text displayed to the user |
| `type` | *object type*: `enum ButtonType`<br>*default value*: `ButtonType.Button`<br>*is required*: `FALSE`<br>The type of the rendered element. Available options are: `Button` (button type="button"), `Submit` (button type="submit"), `LinkAsButton` (link that mimics a button), `ButtonLink` (button that mimics a link) |

## Example

```html
<wv-button type="@ButtonType.Button" text="Save" color="@ErpColor.Primary"></wv-button>
```

