<!--{"sort_order":10, "name": "wv-filter-base", "label": "wv-filter-*"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# wv-filter-* base properties

## Purpose

This is the base filter tag helper. It is inherited by other that are differentiated based on the data they are filtering. Filters in general are participating in search or filter forms and their main purpose is to generate the proper url query for the data in a grid to be filtered.

## Properties

| name | description |
|------|------|
| `field-id` | *object type*: `Guid`<br>*default value*: `Guid.Empty`<br>*is required*: `FALSE`<br>Used as an HTML ID of the field which is need for the Javascript functions |
| `init-errors` | *object type*: `List<string>`<br>*default value*: `new List<string>()`<br>*is required*: `FALSE`<br>If any init errors are set, the field will render the label and an error message. Usually used for showing non validation system errors |
| `label` | *object type*: `string`<br>*default value*: `string.Empty`<br>*is required*: `FALSE`<br>This is the form's label that will be presented to the end user |
| `name` | *object type*: `string`<br>*default value*: `string.Empty`<br>*is required*: `FALSE`<br>Should match the record field name that needs to be filtered |
| `prefix` | *object type*: `string`<br>*default value*: `string.Empty`<br>*is required*: `FALSE`<br>If you have two or more grids on a single page, each grid could apply a prefix to it query parameters it applies. You need to match this prefix in order for the filter to work correctly |
| `query-options` | *object type*: `List<FilterType>`<br>*default value*: `new List<FilterType>()`<br>*is required*: `FALSE`<br>What options are available for filter type to the user. If empty list - the select input will not be presented to the user. If only one option that equals the `query-type` the select will be read-only. Options are: Undefined, STARTSWITH, CONTAINS, EQ, NOT, LT, LTE, GT, GTE, REGEX, FTS, BETWEEN, NOTBETWEEN |
| `query-type` | *object type*: `FilterType`<br>*default value*: `FilterType.Undefined`<br>*is required*: `FALSE`<br>This is the default filter/ query type that will be performed. Options are: Undefined, STARTSWITH, CONTAINS, EQ, NOT, LT, LTE, GT, GTE, REGEX, FTS, BETWEEN, NOTBETWEEN |

## Example

```html
<wv-filter-text id="my-drawer" title="This is a drawer">...</wv-filter-text>
```

