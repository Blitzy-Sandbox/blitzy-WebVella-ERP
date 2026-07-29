<!--{"sort_order":1, "name": "overview", "label": "Overview"}-->
# Overview

## Purpose
WebVella ERP is a set of tools used to model and manage specific business data. In this context an entity is a piece of data defined by a meta, set of fields and a set of relations to other entities. It is very similar to the Entity-Relation model of the standard relational databases. The primary purpose of the entities is to provide an easier and maintainable method of defining data objects, work with them and limit the access to them. The system will automatically apply those rules to all API based requests. It will also create and maintain the optimal database structure. You can create or modify the entity meta and the entity records data through a server API or a web API.

## Entity meta

The entity meta includes the following properties:

| name | description |
|------|------|
| `Color` | *object type*: `string`<br>*default value*: `string.Empty`<br>*is required*: `TRUE`<br>Color code used in visual representation of the entity |
| `IconClass` | *object type*: `string`<br>*default value*: `string.Empty`<br>*is required*: `TRUE`<br>Icon class for generating the icon that visually represent the entity. [Font Awesome Library](http://fontawesome.io/icons/) is supported. |
| `Id` | *object type*: `Guid`<br>*default value*: `auto generated`<br>*is required*: `TRUE`<br>This is database unique id of the entity meta. |
| `Label` | *object type*: `string`<br>*default value*: `string.Empty`<br>*is required*: `TRUE`<br>A text used to reference a single data record of this entity |
| `LabelPlural` | *object type*: `string`<br>*default value*: `string.Empty`<br>*is required*: `TRUE`<br>A text used to reference multiple data records of this entity |
| `Name` | *object type*: `string`<br>*default value*: `string.Empty`<br>*is required*: `TRUE`<br>An unique human readable reference for the entity a.k.a developer name. It will be used as in the APIs and in database table name generation, which is the entity name with a prefix "rec_". |
| `RecordPermissions` | *object type*: `RecordPermissions`<br>*default value*: `Null`<br>*is required*: `FALSE`<br>This defines what user roles can read, create, update or delete records from this entity - a record level security. You can fine grain this control on field level too. |
| `RecordScreenIdField` | *object type*: `Guid?`<br>*default value*: `Null`<br>*is required*: `FALSE`<br>The field that will be used as a reference to a single record from this entity. If Null the ID field's value will be used. |
| `System` | *object type*: `bool`<br>*default value*: `FALSE`<br>*is required*: `TRUE`<br>When creating an application, you will need to define a schema that you need to be managed only by the corresponding plugin and not the administrator. By marking the entity as System, the interface will lock certain changes. |

