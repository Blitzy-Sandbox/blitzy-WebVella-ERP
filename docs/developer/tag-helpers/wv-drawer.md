<!--{"sort_order":10, "name": "wv-drawer", "label": "wv-drawer"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-drawer

## Purpose

`<wv-drawer/>`. Presents a sliding from the right container. The element is operated with the help of JS Event it listens to. 

## Properties

| name | description |
|------|------|
| `body-class` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>A CSS class to be added to the general classes of the element's body. |
| `class` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>A CSS class to be added to the general classes of the element. |
| `id` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Html ID you may need to set to the rendered element |
| `title` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>The title of the drawer. |
| `title-action-html` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Html to be rendered on the right of the title |
| `width` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>If provided, it will be added as a CSS style width value of the element. |

## Javascript Event Listeners
<table>
<thead><tr><th>action</th><th>description</th></tr></thead>
<tbody>
<tr><td><p><code>open</code> or <code>show</code></p></td><td><p>This action will open the drawer. Example:</p><pre><code class="language-javascript">ErpEvent.DISPATCH('WebVella.Erp.Web.Components.PcDrawer','open')</code></pre><p>If there are one or more drawers on the page you need to set the correct <code>htmlId</code> of the drawer's PageComponent</p><pre><code class="language-javascript">ErpEvent.DISPATCH('WebVella.Erp.Web.Components.PcDrawer',{htmlId:HTML_ID,action:'open',payload:null})</code></pre></td></tr>
<tr><td><p><code>close</code> or <code>hide</code></p></td><td><p>This action will close the drawer. Example:</p><pre><code class="language-javascript">ErpEvent.DISPATCH('WebVella.Erp.Web.Components.PcDrawer','close')</code></pre><p>If there are one or more drawers on the page you need to set the correct <code>htmlId</code> of the drawer's PageComponent</p><pre><code class="language-javascript">ErpEvent.DISPATCH('WebVella.Erp.Web.Components.PcDrawer',{htmlId:HTML_ID,action:'close',payload:null})</code></pre></td></tr>
</tbody>
</table>

## Example

```html
<wv-drawer id="my-drawer" title="This is a drawer">...</wv-drawer>
```

