<!--{"sort_order":10, "name": "wv-code-highlight", "label": "wv-code-highlight"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-code-highlight

## Purpose

Helper for implementing [prismjs.com](http://prismjs.com) code highlighter. Install this JS library and its CSS classes before using this Tag helper. 
The library is already installed in this developer's section.

## Properties

| name | description |
|------|------|
| `wv-code-highlight` | *html target*: `attribute`<br>*object type*: `string`<br>*default value*: `language-html`<br>*is required*: `TRUE`<br>Sets the highlighting language based on the install plugins for prism.js and according to the [supported languages](http://prismjs.com/index.html#languages-list) |
| `wv-code-string` | *html target*: `attribute`<br>*object type*: `string`<br>*default value*: `sample html`<br>*is required*: `TRUE`<br>A string variable that provides the source code / html to be rendered. The implementation is done this way, as otherwise MVC will clear any used non standard attributes in the HTML case |

## Example

```html
    <div wv-code-highlight="language-html" wv-code-string="@example1Code"></div>
```

