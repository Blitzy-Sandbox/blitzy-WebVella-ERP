<!--{"sort_order":10, "name": "wv-field-append", "label": "wv-field-append"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-field-append

## Purpose

`<wv-field-append/>`. This is used only in a `<wv-field-*/>` field tag helpers. Provides the ability to render the field with an "input-group" as per Bootstrap CSS specifications. Appends the provided string as html in its proper place. 

## Example

```html
<wv-field-date>
	<wv-field-append><span class='input-group-text'><i class='fa fa-fw fa-calendar-alt'></i></span></wv-field-append>
</wv-field-date>
```

