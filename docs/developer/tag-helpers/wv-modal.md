<!--{"sort_order":10, "name": "wv-modal", "label": "wv-modal"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-modal

## Purpose

`<wv-modal/>`. Presents a modal window. The element is operated with the help of JS Event it listens to. Used in conjunction with `<wv-modal-body/>` and `<wv-modal-footer/>`

## Properties

| name | description |
|------|------|
| `backdrop` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Sets the modal's backdrop property according to the Bootstrap reference. Options are: `true`, `false` or `static`. Includes a modal-backdrop element. Alternatively, specify static for a backdrop which doesn't close the modal on click. |
| `id` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Html ID you may need to set to the rendered element |
| `position` | *object type*: `ModalPosition`<br>*default value*: `ModalPosition.Top`<br>*is required*: `FALSE`<br>Sets the modal position in the viewport. Options are: Top, VerticallyCentered |
| `size` | *object type*: `ModalSize`<br>*default value*: `ModalSize.Normal`<br>*is required*: `FALSE`<br>Sets the modal width size. Options are: Normal, Small, Large, ExtraLarge, Full |
| `title` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>The title of the modal. |


## Javascript Event Listeners
<table>
<thead><tr><th>action</th><th>description</th></tr></thead>
<tbody>
<tr><td><p><code>open</code> or <code>show</code></p></td><td><p>This action will open the modal. Example:</p><pre><code class="language-javascript">ErpEvent.DISPATCH('WebVella.Erp.Web.Components.PcModal','open')</code></pre><p>If there are one or more modals on the page you need to set the correct <code>htmlId</code> of the modal's PageComponent</p><pre><code class="language-javascript">ErpEvent.DISPATCH('WebVella.Erp.Web.Components.PcModal',{htmlId:HTML_ID,action:'open',payload:null})</code></pre></td></tr>
<tr><td><p><code>close</code> or <code>hide</code></p></td><td><p>This action will close the modal. Example:</p><pre><code class="language-javascript">ErpEvent.DISPATCH('WebVella.Erp.Web.Components.PcModal','close')</code></pre><p>If there are one or more modals on the page you need to set the correct <code>htmlId</code> of the modal's PageComponent</p><pre><code class="language-javascript">ErpEvent.DISPATCH('WebVella.Erp.Web.Components.PcModal',{htmlId:HTML_ID,action:'close',payload:null})</code></pre></td></tr>
</tbody>
</table>

## Example

```html
<wv-modal title="SQL Result" id="modal-sql-result" size="Large">
	<wv-modal-body>
		...
	</wv-modal-body>
	<wv-modal-footer>
		...
	</wv-modal-footer>
</wv-modal>
```

