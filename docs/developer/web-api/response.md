<!--{"sort_order":1, "name": "response", "label": "Response Format"}-->
# Web API Response Format

All responses are in JSON formatted in a specific way.

> **Legacy response envelope (verified).** The JSON examples and the property
> table below document the platform's existing `BaseResponseModel` response
> envelope. The target `/api/v1/` response contract is **Not available / to be
> confirmed** — there is no `WebVella.Erp.Api` project yet. `BaseResponseModel`
> carries seven members — `timestamp`, `success`, `message`, `hash`, `errors`,
> `accessWarnings`, and (via `ResponseModel`) `object` — all of which are shown
> below.
>
> Source: /WebVella.Erp/Api/Models/BaseModels.cs:L8-L48 (BaseResponseModel incl. hash + accessWarnings; ResponseModel.object), L50-L59 (AccessWarningModel), L62-L71 (ErrorModel).

## Responding to GET a single entity record

```json
{
  "success": true,
  "message": "Nisi proident tempor cillum sint duis eu elit dolor Lorem amet qui officia occaecat.",
  "timestamp": "2014-03-03T23:20:23Z",
  "hash": null,
  "errors": [],
  "accessWarnings": [],
  "object": {
    "id": 1,
  }
}
```

## Returning to GET a list of objects

```json
{
  "success": true,
  "message": "Nisi proident tempor cillum sint duis eu elit dolor Lorem amet qui officia occaecat.",
  "timestamp": "2014-03-03T23:20:23Z",
  "hash": null,
  "errors": [],
  "accessWarnings": [],
  "object": [
	{
		"id": 1,
	},
	{
		"id": 1,
	}
  ]
}
```

## Returning to POST,PUT,DELETE an object

```json
{
  "success": false,
  "message": "Nisi proident tempor cillum sint duis eu elit dolor Lorem amet qui officia occaecat.",
  "timestamp": "2014-03-03T23:20:23Z",
  "hash": null,
  "errors": [
    {
      "key": "url",
      "value": "",
      "message": "URL cannot be blank"
    }  
  ],
  "accessWarnings": [],
  "object": {
		"id": 1,
	}
}
```

## Properties

> The **proposed** target `/api/v1/` error/problem-details model (Not available / to be confirmed) is described in [`../../api-reference/errors.md`](../../api-reference/errors.md).

| name | description |
| --- | --- |
| `accessWarnings` | *object type*: `List<AccessWarningModel>`<br/><br/>*default value*: ``<br/><br/>list of access-warning objects reported when a record or field is returned with restricted access. It is empty when no warnings are reported. The object format is:<br/>* key - the access key/context of the warning<br/>* code - a machine-readable warning code<br/>* message - human readable message of the warning |
| `errors` | *object type*: `List<ErrorModel>`<br/><br/>*default value*: ``<br/><br/>list of error objects returned during the method execution. It is empty when no errors are reported. The object format is:<br/>* key - the property name, if any, which validation or execution returned an error<br/>* value - the property value, that causes the problem<br/>* message - human readable message of the error |
| `hash` | *object type*: `string`<br/><br/>*default value*: `null`<br/><br/>an optional hash value associated with the response. It is `null` by default. |
| `message` | *object type*: `string`<br/><br/>*default value*: `Success`<br/><br/>Method execution result in human readable form. Often provided to the end-user as a feedback |
| `object` | *object type*: `object`<br/><br/>*default value*: `Success`<br/><br/>The object returned by the method. |
| `success` | *object type*: `bool`<br/><br/>*default value*: `true`<br/><br/>Whether the method execution is successfully completed |
| `timestamp` | *object type*: `DateTime`<br/><br/>*default value*: ``<br/><br/>when the method was executed in ISO 8601 date string and UTC time zone |
