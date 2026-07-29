<!--{"sort_order":10, "name": "wv-field-image", "label": "wv-field-image"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-field-image

## Purpose

`<wv-field-image/>`. Provides the ability to render the image field type of an Erp Entity. It uploads files to the server based on the configuration - in the database or on local storage. Have in mind that server file and media paths require a file controller to be access, usually you need to add "/fs" controller before the path.

## Properties
**Important**: All `<wv-field-*>` helpers inherit a ["base tag helper" properties](wv-field-base.md). In the following list are presented only the properties that this tag helper adds or alters. Not all base tag helper properties can be implemented by this tag helper too.

| name | description |
|------|------|
| `accept` | *object type*: `string`<br>*default value*: `string.empty`<br>*is required*: `FALSE`<br>string of the accepted file extensions. For reference you can check out the [html attribute definition page](https://www.w3schools.com/tags/att_input_accept.asp). |
| `height` | *object type*: `int?`<br>*default value*: `NULL`<br>*is required*: `FALSE`<br>the requested image height. The server will automatically resize and cache the new copy of the image |
| `resize-action` | *object type*: `ImageResizeMode`<br>*default value*: `ImageResizeMode.Pad`<br>*is required*: `FALSE`<br>the resize action type that needs to be taken when both width and height are set. Options are: Pad, BoxPad, Crop, Min, Max, Stretch. For more information please check the [ImageProcessor reference page (Wayback Machine archive)](https://web.archive.org/web/20180730162608/http://imageprocessor.org/imageprocessor-web/imageprocessingmodule/resize/) — the original `imageprocessor.org` site is no longer online |
| `text-remove` | *object type*: `string`<br>*default value*: `remove`<br>*is required*: `FALSE`<br>the text for the remove image link |
| `text-select` | *object type*: `string`<br>*default value*: `select`<br>*is required*: `FALSE`<br>the text for the select image link |
| `width` | *object type*: `int?`<br>*default value*: `NULL`<br>*is required*: `FALSE`<br>the requested image width. The server will automatically resize and cache the new copy of the image |


## Example

```html
<wv-field-image value="@value"></wv-field-image>
```

