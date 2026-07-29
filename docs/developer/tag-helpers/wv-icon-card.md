<!--{"sort_order":10, "name": "wv-icon-card", "label": "wv-icon-card"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-icon-card

## Purpose

`<wv-icon-card/>`. Generates a styled icon card.

## Properties

| name | description |
|------|------|
| `class` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `FALSE`<br>Added to the generated card classes |
| `description` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `FALSE`<br>Description of the card |
| `icon-class` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `FALSE`<br>Used to generate the icon of the card using [FontAwesome icon library](https://fontawesome.com/icons) |
| `icon-color` | *object type*: `ErpColor`<br>*default value*: `ErpColor.Default`<br>*is required*: `FALSE`<br>Select from 32 color options |
| `is-card` | *object type*: `bool`<br>*default value*: `TRUE`<br>*is required*: `FALSE`<br>If TRUE generates the card wrapping lines according to the Bootstrap styling |
| `is-clickable` | *object type*: `bool`<br>*default value*: `TRUE`<br>*is required*: `FALSE`<br>If TRUE adds a "clickable" class that will change the cursor as pointer on hovering the card |
| `has-shadow` | *object type*: `bool`<br>*default value*: `TRUE`<br>*is required*: `FALSE`<br>If TRUE generates a shadow below the card |
| `title` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `FALSE`<br>Title of the card |



## Example

```html
<wv-icon-card title="Database" class="mb-4" description="SQL Select" icon-class="fas fa-fw fa-database" icon-color="Purple"></wv-icon-card>
```

