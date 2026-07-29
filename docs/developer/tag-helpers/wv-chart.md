<!--{"sort_order":10, "name": "wv-chart", "label": "wv-chart"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-chart

## Purpose

`<wv-chart/>`. Used to render charts by using the [Chart JS](https://www.chartjs.org/) Javascript library. 

## Properties

| name | description |
|------|------|
| `datasets` | *object type*: `List<ErpChartDataset>`<br>*default value*: `new List<ErpChartDataset>()`<br>*is required*: `TRUE`<br>Data for the charts, background and border colors provided in a specific format needed by the library |
| `height` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>If provided, it will be added as a CSS style height value of the chart's wrapper element. |
| `id` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>Html ID you may need to set to the rendered element |
| `labels` | *object type*: `List<string>`<br>*default value*: `new List<string>()`<br>*is required*: `TRUE`<br>Labels corresponding to the dataset values |
| `show-legend` | *object type*: `bool`<br>*default value*: `FALSE`<br>*is required*: `FALSE`<br>Whether to render the chart's legend |
| `type` | *object type*: `enum ErpChartType`<br>*default value*: `ErpChartType.Line`<br>*is required*: `FALSE`<br>The type of the chart that needs to be rendered. Options are: Line, Bar, Pie, Doughnut, Area, HorizontalBar |
| `width` | *object type*: `string`<br>*default value*: `String.Empty`<br>*is required*: `FALSE`<br>If provided, it will be added as a CSS style width value of the chart's wrapper element. |


## Example

```html
<wv-chart type="@ErpChartType.Line" datasets="@Datasets"></wv-chart>
```

