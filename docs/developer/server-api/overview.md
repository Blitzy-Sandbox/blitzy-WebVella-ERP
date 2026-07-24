<!--{"sort_order":1, "name": "overview", "label": "Overview"}-->
# Overview

The server API is implemented by the following classes

**Note:** These are **in-process C# managers** — **EntityManager**, **EntityRelationManager**, **RecordManager**, and **SecurityManager** — that remain **unchanged** and continue to run **in-process** in the headless platform. They are distinct from the new `/api/v1/` REST surface, which internally delegates to these same managers. Use them directly from plugin or server-side code; remote/HTTP consumers should use the REST API instead. For the REST surface that wraps these managers, see the [API Reference](../../api-reference/index.md). Source: /docs/developer/server-api/overview.md

## EntityManager

Entity meta and entity field related operations. **Important:** Requires `Administration` role

```csharp
Entity entity = new EntityManager().ReadEntity("user").Object;
```

## EntityRelationManager

Entity relations operation. **Important:** Requires `Administration` role

```csharp
List<EntityRelation> relationList = new EntityRelationManager().Read(storageEntityList).Object;
```

## RecordManager

Operations with entity records. **Important:** The access depends on the preferences selected in the corresponding entity

```csharp
var createResponse = new RecordManager().CreateRecord("offer", PostObject);
```

## SecurityManager

```csharp
var user = new SecurityManager().GetUser(userId);
```

User and User role related operations