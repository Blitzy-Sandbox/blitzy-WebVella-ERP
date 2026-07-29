<!--{"sort_order":5, "name": "entity-relations", "label": "Entity Relations"}-->
# Entity Relations

Multiple relations can exists for each entity. They can be either "1:N" (one to many) or "N:N" (many to many). By establishing such relations you can:

* select easy related data
* put database constrains to ensure proper relation

## Properties

| name | description |
|------|------|
| `Id` | *object type*: `Guid`<br>*default value*: `Null`<br>*is required*: `TRUE`<br>Unique key for identifying the relation |
| `Name` | *object type*: `string`<br>*default value*: `Null`<br>*is required*: `TRUE`<br>Human readable identification of the relation. Used in EQL selections by adding "$" prefix |
| `OriginEntityId` | *object type*: `Guid`<br>*default value*: `Null`<br>*is required*: `TRUE`<br>The Id of the entity which originates the relation |
| `OriginFieldId` | *object type*: `Guid`<br>*default value*: `Null`<br>*is required*: `TRUE`<br>The Id of the field in the entity which originates the relation |
| `OriginEntityName` | *object type*: `string`<br>*default value*: `Null`<br>*is required*: `TRUE`<br>GET only. Convenience property. The name of the entity which originates the relation |
| `OriginFieldName` | *object type*: `string`<br>*default value*: `Null`<br>*is required*: `TRUE`<br>GET only. Convenience property. The name of the field in the entity which originates the relation |
| `RelationType` | *object type*: `EntityRelationType`<br>*default value*: `Null`<br>*is required*: `TRUE`<br>Sets the relation type. Options: OneToMany, ManyToMany |
| `System` | *object type*: `bool`<br>*default value*: `Null`<br>*is required*: `FALSE`<br>When creating an application, you will need to define a schema that you need to be managed only by the corresponding plugin and not the administrator. By marking the relation as System, the interface will lock certain changes. |
| `TargetEntityId` | *object type*: `Guid`<br>*default value*: `Null`<br>*is required*: `TRUE`<br>The Id of the entity which is the relation target |
| `TargetFieldId` | *object type*: `Guid`<br>*default value*: `Null`<br>*is required*: `TRUE`<br>The Id of the field in the entity which is the relation target |
| `TargetEntityName` | *object type*: `string`<br>*default value*: `Null`<br>*is required*: `TRUE`<br>GET only. Convenience property. The name of the entity which is the relation target |
| `TargetFieldName` | *object type*: `string`<br>*default value*: `Null`<br>*is required*: `TRUE`<br>GET only. Convenience property. The name of the field in the entity which is the relation target |