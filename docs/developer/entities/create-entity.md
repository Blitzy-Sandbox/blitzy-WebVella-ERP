<!--{"sort_order":2, "name": "create-entity", "label": "Create entity"}-->
# Create entity

## Service Side API

The API class that is needed for entity, entity meta and field creation is `EntityManager` with its `CreateEntity` method. To initiate this, you need to be in `administrator` role. You can create an entity with a code similar to:

```csharp
var userEntity = new InputEntity();
userEntity.Name = "user";
userEntity.Label = "User";
userEntity.LabelPlural = "Users";
userEntity.System = true;
userEntity.Color = "#f44336";
userEntity.IconName = "ti-user";
EntityResponse response = new EntityManager().CreateEntity(userEntity);
```

## Web API

See the planned `/api/v1/` REST reference (target — not yet implemented): `../../api-reference/entities.md`.

##### Authorization

To initiate this web request, you need to be in the `administrator` role.

##### HTTP request
```http
POST https://<YOUR_DOMAIN>/api/v3/en_US/meta/entity
```
This is the current route served by the in-process host, gated to the `administrator` role. Source: /WebVella.Erp.Web/Controllers/WebApiController.cs:L1473,L1475. The planned headless surface would expose it under `/api/v1/meta/entity`; that surface is not yet implemented.

##### Query parameters

No query parameters are required with this method.

##### Request body

You need to post a `InputEntity` object as a request body.

##### Request response

If successful, this method returns a response JSON with the following structure:

```json
{
  "timestamp": "2014-03-03T23:20:23Z",
  "success": false,
  "message": "Aliqua anim consequat amet cupidatat proident amet amet.",
  "errors": [
    {
      "key": "fieldName",
      "value": "evaluated value",
      "message": "Error message"
    }
  ],
  "object": {
	/// The newly created entity
  }
}
```


## Web interface

**Important**: This page describes a process of creating an entity using the `WebVella SDK Plugin` web interface


##### Step 1: Navigate to the SDK Application

##### Step 2: Select from the Objects top menu -> Entities

![Entity list](/doc-images/sdk-entity-list.png)

##### Step 3: Press the "Create Entity" button on the top right corner

![Entity list](/doc-images/sdk-entity-create.png)

##### Step 4: Press the green "Create Entity" button